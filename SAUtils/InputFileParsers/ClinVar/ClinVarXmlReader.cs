﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using CommandLine.Utilities;
using Compression.Utilities;
using SAUtils.DataStructures;
using VariantAnnotation.Interface.Providers;
using VariantAnnotation.Interface.Sequence;

namespace SAUtils.InputFileParsers.ClinVar
{
    public sealed class ClinvarVariant
	{
		public readonly IChromosome Chromosome;
		public int Start { get; }
		public readonly int Stop;
		public readonly string ReferenceAllele;
		public readonly string AltAllele;
		public string VariantType;
	    public readonly List<string> AllelicOmimIds;

		public ClinvarVariant(IChromosome chr, int start, int stop, string refAllele, string altAllele, List<string> allilicOmimIds =null)
		{
			Chromosome      = chr;
			Start           = start;
			Stop            = stop;
			ReferenceAllele = refAllele ?? "";
			AltAllele       = altAllele ?? "";
            AllelicOmimIds  = allilicOmimIds ?? new List<string>();
		}

	    public override int GetHashCode()
	    {
	        return Chromosome.GetHashCode()
	               ^ ReferenceAllele.GetHashCode()
	               ^ AltAllele.GetHashCode()
	               ^ Start
	               ^ Stop;
	    }

	    public override bool Equals(object obj)
	    {
	        if (!(obj is ClinvarVariant other)) return false;

	        return Chromosome.Equals(other.Chromosome)
	               && Start == other.Start
	               && Stop == other.Stop
	               && ReferenceAllele.Equals(other.ReferenceAllele)
	               && AltAllele.Equals(other.AltAllele);
	    }
	}

	public sealed class ClinVarXmlReader : IEnumerable<ClinVarItem>
    {
        #region members

        private readonly FileInfo _clinVarXmlFileInfo;
		private readonly VariantAligner _aligner;
        private readonly ISequenceProvider _sequenceProvider;
        private readonly IDictionary<string, IChromosome> _refChromDict;

        #endregion

        #region xmlTags

        const string ClinVarSetTag = "ClinVarSet";

        #endregion

        #region clinVarItem fields

        private readonly List<ClinvarVariant> _variantList= new List<ClinvarVariant>();
		private HashSet<string> _alleleOrigins;
		private string _reviewStatus;
		private string _id;
		private HashSet<string> _prefPhenotypes;
		private HashSet<string> _altPhenotypes;
		private string _significance;

		private HashSet<string> _medGenIDs;
		private HashSet<string> _omimIDs;
        private HashSet<string> _allilicOmimIDs;
		private HashSet<string> _orphanetIDs;

		HashSet<long> _pubMedIds= new HashSet<long>();
		private long _lastUpdatedDate;

		#endregion

		private bool _hasDbSnpId;

        private void ClearClinvarFields()
		{
			_variantList.Clear();
			_reviewStatus      = null;
			_alleleOrigins     = new HashSet<string>();
			_significance      = null;
			_prefPhenotypes    = new HashSet<string>();
			_altPhenotypes     = new HashSet<string>();
			_id                = null;
			_medGenIDs         = new HashSet<string>();
			_omimIDs           = new HashSet<string>();
            _allilicOmimIDs    = new HashSet<string>();
            _orphanetIDs       = new HashSet<string>();
			_pubMedIds         = new HashSet<long>();//we need a new pubmed hash since otherwise, pubmedid hashes of different items interfere. 
			_lastUpdatedDate   = long.MinValue;
		    _hasDbSnpId = false;

		}

		#region IEnumerable implementation

		public IEnumerator<ClinVarItem> GetEnumerator()
        {
            return GetItems().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion

        // constructor
        public ClinVarXmlReader(FileInfo clinVarXmlFileInfo, ISequenceProvider sequenceProvider)
        {
            _sequenceProvider = sequenceProvider;
            _aligner = new VariantAligner(_sequenceProvider.Sequence);
            _clinVarXmlFileInfo = clinVarXmlFileInfo;
            _refChromDict = sequenceProvider.GetChromosomeDictionary();
        }

		/// <summary>
		/// Parses a ClinVar file and return an enumeration object containing all the ClinVar objects
		/// that have been extracted
		/// </summary>
		private IEnumerable<ClinVarItem> GetItems()
		{
			using (var reader = GZipUtilities.GetAppropriateStreamReader(_clinVarXmlFileInfo.FullName))
			using (var xmlReader = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true}))
			{
				//skipping the top level element to go down to its elementren
			    xmlReader.ReadToDescendant(ClinVarSetTag);

			    var benchmark = new Benchmark();
			    var itemCount = 0;
				do
				{
					var subTreeReader = xmlReader.ReadSubtree();
				    var xElement = XElement.Load(subTreeReader);

                    var clinVarItems = ExtractClinVarItems(xElement);

					if (clinVarItems == null || clinVarItems.Count==0) continue;

					foreach (var clinVarItem in clinVarItems)
					{
					    itemCount++;
						yield return clinVarItem;
					}

                    if (itemCount%10_000==0)
                        Console.WriteLine($"processed {itemCount} clinvar entries in {Benchmark.ToHumanReadable(benchmark.GetElapsedTime())}");
				} while (xmlReader.ReadToNextSibling(ClinVarSetTag));
			}
			
		}

        private const string RefAssertionTag = "ReferenceClinVarAssertion";
        private const string ClinVarAssertionTag = "ClinVarAssertion";
        private List<ClinVarItem> ExtractClinVarItems(XElement xElement)
		{
            ClearClinvarFields();

			if (xElement == null || xElement.IsEmpty) return null;

			foreach (var element in xElement.Elements(RefAssertionTag))
			    ParseRefClinVarAssertion(element);

		    foreach (var element in xElement.Elements(ClinVarAssertionTag))
                ParseClinvarAssertion(element);
		    

			var clinvarList = new List<ClinVarItem>();
            var variants = new HashSet<ClinvarVariant>();
			foreach (var variant in _variantList)
			{
                if(variant.Chromosome == null) continue;

                if ((variant.VariantType == "Microsatellite" || variant.VariantType=="Variation")
                    && string.IsNullOrEmpty(variant.AltAllele)) continue;

                _sequenceProvider.LoadChromosome(variant.Chromosome);


                if (!ValidateRefAllele(variant)) continue;
                

				ClinvarVariant shiftedVariant= variant;
				//some entries do not have ref allele in the xml file. For those, we extract them from our ref sequence
				if (string.IsNullOrEmpty(variant.ReferenceAllele) && variant.VariantType=="Deletion" )
					shiftedVariant = GenerateRefAllele(variant, _sequenceProvider.Sequence);
				if (string.IsNullOrEmpty(variant.AltAllele) && variant.VariantType == "Duplication")
					shiftedVariant = GenerateAltAllele(variant, _sequenceProvider.Sequence);

				

				//left align the variant
				shiftedVariant = LeftShift(shiftedVariant);
                
                if (string.IsNullOrEmpty(variant.ReferenceAllele) && variant.VariantType == "Indel" && !string.IsNullOrEmpty(variant.AltAllele))
					shiftedVariant = GenerateRefAllele(variant, _sequenceProvider.Sequence);

				if(string.IsNullOrEmpty(shiftedVariant.ReferenceAllele) && string.IsNullOrEmpty(shiftedVariant.AltAllele)) continue;

                //getting the unique ones
			    variants.Add(shiftedVariant);
                
			}

		    foreach (var clinvarVariant in variants)
		    {
		        var extendedOmimIds = new HashSet<string>(_omimIDs);

		        foreach (var omimId in clinvarVariant.AllelicOmimIds)
		        {
		            extendedOmimIds.Add(omimId);
                }

		        clinvarList.Add(
		            new ClinVarItem(clinvarVariant.Chromosome,
		                clinvarVariant.Start,
		                _alleleOrigins.ToList(),
		                clinvarVariant.AltAllele ,
		                _id,
		                _reviewStatus,
		                _medGenIDs.ToList(),
		                extendedOmimIds.ToList(),
		                _orphanetIDs.ToList(),
		                _prefPhenotypes.Count > 0 ? _prefPhenotypes.ToList() : _altPhenotypes.ToList(),
		                clinvarVariant.ReferenceAllele ,
		                _significance,
		                _pubMedIds.OrderBy(x=>x).ToList(),
		                _lastUpdatedDate));
            }

			return clinvarList.Count > 0 ? clinvarList: null;
		}

	    private bool ValidateRefAllele(ClinvarVariant clinvarVariant)
	    {
	        if (string.IsNullOrEmpty(clinvarVariant.ReferenceAllele) || clinvarVariant.ReferenceAllele == "-") return true;

		    var refAllele = clinvarVariant.ReferenceAllele;
		    if (string.IsNullOrEmpty(refAllele)) return true;

		    var refLength = clinvarVariant.Stop - clinvarVariant.Start + 1;
		    if (refLength != refAllele.Length) return false;

		    return _sequenceProvider.Sequence.Validate(clinvarVariant.Start, clinvarVariant.Stop, refAllele);

	    }

	    private static ClinvarVariant GenerateAltAllele(ClinvarVariant variant, ISequence compressedSequence)
		{
			if (variant == null) return null;
			var extractedAlt = compressedSequence.Substring(variant.Start - 1, variant.Stop - variant.Start + 1);

            return new ClinvarVariant(variant.Chromosome, variant.Start, variant.Stop, variant.ReferenceAllele , extractedAlt, variant.AllelicOmimIds);
		}

		private static ClinvarVariant GenerateRefAllele(ClinvarVariant variant, ISequence compressedSequence)
		{
			if (variant == null) return null;
			var extractedRef = compressedSequence.Substring(variant.Start - 1, variant.Stop - variant.Start + 1);

            return new ClinvarVariant(variant.Chromosome, variant.Start, variant.Stop, extractedRef, variant.AltAllele, variant.AllelicOmimIds);

		}

		private ClinvarVariant LeftShift(ClinvarVariant variant)
		{
			if (variant.ReferenceAllele == null || variant.AltAllele == null) return variant;

			var alignedVariant = _aligner.LeftAlign(variant.Start, variant.ReferenceAllele, variant.AltAllele);
			if (alignedVariant == null) return variant;

		    return new ClinvarVariant(variant.Chromosome, alignedVariant.Item1, variant.Stop, alignedVariant.Item2,alignedVariant.Item3, variant.AllelicOmimIds);
		}

		internal static long ParseDate(string s)
		{
			if (string.IsNullOrEmpty(s) || s == "-") return long.MinValue;
			//Jun 29, 2010
			return DateTime.Parse(s).Ticks;
		}

        private const string UpdateDateTag= "DateLastUpdated";
        private const string AccessionTag = "Acc";
        private const string VersionTag = "Version";
        private const string ClinVarAccessionTag = "ClinVarAccession";
        private const string ClinicalSignificanceTag = "ClinicalSignificance";
        private const string MeasureSetTag = "MeasureSet";
        private const string TraitSetTag = "TraitSet";
        private const string ObservedInTag = "ObservedIn";
        private const string SampleTag = "Sample";

        private void ParseRefClinVarAssertion(XElement xElement)
		{
			if (xElement==null || xElement.IsEmpty) return;
			//<ReferenceClinVarAssertion DateCreated="2013-10-28" DateLastUpdated="2016-04-20" ID="182406">
		    _lastUpdatedDate = ParseDate(xElement.Attribute(UpdateDateTag)?.Value);
		    _id              = xElement.Element(ClinVarAccessionTag)?.Attribute(AccessionTag)?.Value + "." + xElement.Element(ClinVarAccessionTag)?.Attribute(VersionTag)?.Value;

            GetClinicalSignificance(xElement.Element(ClinicalSignificanceTag));
		    ParseMeasureSet(xElement.Element(MeasureSetTag));
		    ParseTraitSet(xElement.Element(TraitSetTag));
		}

        private const string CitationTag = "Citation";
        private const string OriginTag = "Origin";

        private void ParseClinvarAssertion(XElement xElement)
		{
		    if (xElement == null || xElement.IsEmpty) return;

            foreach (var element in xElement.Descendants(CitationTag))
				ParseCitation(element);

		    foreach (var element in xElement.Elements(ObservedInTag))
                ParseObservedIn(element);

        }

        private void ParseObservedIn(XElement xElement)
        {
            var samples = xElement?.Elements(SampleTag);
            if (samples == null) return;

            foreach (var sample in samples)
            {
                foreach (var origin in sample.Elements(OriginTag))
                    _alleleOrigins.Add(origin.Value);
            }
        }

        private const string TraitTag = "Trait";

        private void ParseTraitSet(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

			foreach (var element in xElement.Elements(TraitTag))
			    ParseTrait(element);
		}

        private const string NameTag = "Name";
		private void ParseTrait(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

		    foreach (var element in xElement.Elements(XrefTag))
		        ParseXref(element);

		    ParsePnenotype(xElement.Element(NameTag));
		}

        private const string ElementValueTag = "ElementValue";
        private const string XrefTag = "XRef";
        private void ParsePnenotype(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

		    var isPreferred = ParsePhenotypeElementValue(xElement.Element(ElementValueTag));
		    if (!isPreferred)
		        return;//we do not want to parse XRef for alternates

		    foreach (var element in xElement.Elements(XrefTag))
                ParseXref(element);
		}

        private const string TypeTag = "Type";

        private bool ParsePhenotypeElementValue(XElement xElement)
		{
		    var phenotype = xElement.Attribute(TypeTag);
		    if (phenotype == null) return false;

			if (phenotype.Value == "Preferred") 
				_prefPhenotypes.Add(xElement.Value);
			if (phenotype.Value == "Alternate")
				_altPhenotypes.Add(xElement.Value);

		    return phenotype.Value == "Preferred";
		}


        private const string DbTag = "DB";
        private const string IdTag = "ID";
        private void ParseXref(XElement xElement)
        {
            var db = xElement.Attribute(DbTag);

            if (db == null) return;

			var id = xElement.Attribute(IdTag)?.Value;//.Trim(' ');

			switch (db.Value)
			{
				case "MedGen":
					_medGenIDs.Add(id);
					break;
				case "Orphanet":
					_orphanetIDs.Add(id);
					break;
				case "OMIM":
				    var type = xElement.Attribute(TypeTag);
					if (type !=null)
					    if (type.Value == "Allelic variant" )
                            _allilicOmimIDs.Add(TrimOmimId(id));
                        else
                            _omimIDs.Add(TrimOmimId(id));
					break;
				case "dbSNP":
				    _hasDbSnpId = true;
					break;
			}
		}

        
        private String TrimOmimId(string id)
	    {
		    return id.TrimStart('P','S');
	    }

        private const string SourceTag = "Source";
        private const string PubmedIdTag = "PubMed";

        private void ParseCitation(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

			
			foreach (var element in xElement.Elements(IdTag))
			{
			    var source = element.Attribute(SourceTag);
			    if (source == null) continue;

			    if (source.Value != PubmedIdTag) continue;

			    var pubmedId = element.Value.TrimEnd('.');
			    if (long.TryParse(pubmedId, out long l) && l <= 99_999_999)//pubmed ids with more than 8 digits are bad
			        _pubMedIds.Add(l);
			    else Console.WriteLine($"WARNING:unexpected pubmedID {pubmedId}.");
                
    		}
		}

        private const string MeasureTag = "Measure";

        private void ParseMeasureSet(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

		    foreach (var element in xElement.Elements(MeasureTag))
		    {
		        ParseMeasure(element);
            }
            
		}


        private const string SeqLocationTag = "SequenceLocation";
        private void ParseMeasure(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

			_hasDbSnpId = false;
            _allilicOmimIDs.Clear();

			//the variant type is available in the attributes
            string varType = xElement.Attribute(TypeTag)?.Value;

			var variantList = new List<ClinvarVariant>();

		    foreach (var element in xElement.Elements(XrefTag))
                ParseXref(element);

		    foreach (var element in xElement.Elements(SeqLocationTag))
            {
		        var variant = GetClinvarVariant(element, _sequenceProvider.GenomeAssembly, _refChromDict);

		        if (variant == null) continue;

		        variant.VariantType = varType;
		        if (variant.AltAllele != null && variant.AltAllele.Length == 1 && _iupacBases.ContainsKey(variant.AltAllele[0]))
		            AddIupacVariants(variant, variantList);
		        else
		            variantList.Add(variant);
		    }

            if (! _hasDbSnpId)
            {
                _variantList.Clear();
                return;
            }

            if (_allilicOmimIDs.Count != 0) 
            {
                foreach (var variant in variantList)
                {
                    variant.AllelicOmimIds.AddRange(_allilicOmimIDs);
                }
            }
            _variantList.AddRange(variantList);
		    
		}

        
        private void AddIupacVariants(ClinvarVariant variant, List<ClinvarVariant> variantList)
		{
			foreach (var altAllele in _iupacBases[variant.AltAllele[0]])
			{
			    variantList.Add(new ClinvarVariant(variant.Chromosome,variant.Start, variant.Stop, variant.ReferenceAllele, altAllele.ToString()));
			}
		}

        private readonly Dictionary<char, char[]> _iupacBases = new Dictionary<char, char[]>
        {
			['R'] = new[] {'A','G'},
			['Y'] = new[] { 'C', 'T' },
			['S'] = new[] { 'G', 'C' },
			['W'] = new[] { 'A', 'T' },
			['K'] = new[] { 'G', 'T' },
			['M'] = new[] { 'A', 'C' },
			['B'] = new[] { 'C', 'G', 'T' },
			['D'] = new[] { 'A', 'G', 'T' },
			['H'] = new[] { 'A', 'C', 'T' },
			['V'] = new[] { 'A', 'C', 'G' }
		};

        private const string ChrTag       = "Chr";
        private const string StopTag      = "display_stop";
        private const string StartTag     = "display_start";
        private const string AssemblyTag  = "Assembly";
        private const string RefAlleleTag = "referenceAllele";
        private const string AltAlleleTag = "alternateAllele";

        private static ClinvarVariant GetClinvarVariant(XElement xElement, GenomeAssembly genomeAssembly,IDictionary<string,IChromosome> refChromDict)
		{
		    if (xElement == null ) return null;//|| xElement.IsEmpty) return null;
			//<SequenceLocation Assembly="GRCh38" Chr="17" Accession="NC_000017.11" start="43082402" stop="43082402" variantLength="1" referenceAllele="A" alternateAllele="C" />

			if (genomeAssembly.ToString()!= xElement.Attribute(AssemblyTag)?.Value
                && genomeAssembly != GenomeAssembly.Unknown) return null;

            var chromosome      = refChromDict.ContainsKey(xElement.Attribute(ChrTag)?.Value) ? refChromDict[xElement.Attribute(ChrTag)?.Value] : null;
            var start           = Convert.ToInt32(xElement.Attribute(StartTag)?.Value);
		    var stop            = Convert.ToInt32(xElement.Attribute(StopTag)?.Value);
		    var referenceAllele = xElement.Attribute(RefAlleleTag)?.Value;
		    var altAllele       = xElement.Attribute(AltAlleleTag)?.Value;

            AdjustVariant(ref start,ref stop, ref referenceAllele, ref altAllele);
		    
            return new ClinvarVariant(chromosome, start, stop, referenceAllele, altAllele);
		}

		private static void AdjustVariant(ref int start, ref int stop, ref string referenceAllele, ref string altAllele)
		{
            if (referenceAllele == "-" && !string.IsNullOrEmpty(altAllele) && stop == start + 1)
            {
				referenceAllele = "";
				start++;
			}

			if (altAllele == "-")
				altAllele = "";
		}

        private const string ReviewStatusTag = "ReviewStatus";
        private const string DescriptionTag = "Description";

        private void GetClinicalSignificance(XElement xElement)
		{
			if (xElement == null || xElement.IsEmpty) return;

		    _reviewStatus = xElement.Element(ReviewStatusTag)?.Value;
		    _significance = xElement.Element(DescriptionTag)?.Value;
		}
    }
}
