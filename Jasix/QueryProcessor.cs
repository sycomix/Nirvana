﻿using System;
using System.Collections.Generic;
using System.IO;
using Jasix.DataStructures;
using Newtonsoft.Json;
using OptimizedCore;
using VariantAnnotation.Algorithms;

namespace Jasix
{
	public sealed class QueryProcessor:IDisposable
	{
		#region members
		private readonly StreamReader _jsonReader;
	    private readonly StreamWriter _writer;
		private readonly Stream _indexStream;
		private readonly JasixIndex _jasixIndex;

        #endregion

        #region IDisposable
	    public void Dispose()
	    {
	        _jsonReader?.Dispose();
	        _writer?.Dispose();
	        _indexStream?.Dispose();
	    }
        #endregion

        public QueryProcessor(StreamReader jsonReader, Stream indexStream, StreamWriter writer=null)
		{
			_jsonReader  = jsonReader;
		    _writer      = writer ?? new StreamWriter(Console.OpenStandardOutput());
			_indexStream = indexStream;
			_jasixIndex  = new JasixIndex(_indexStream);

		}

		public string GetHeader()
		{
			return _jasixIndex.HeaderLine;
		}

        public void PrintChromosomeList()
		{
			foreach (string chrName in _jasixIndex.GetChromosomeList())
			{
                _writer.WriteLine(chrName);
			}
		}

		public void PrintHeaderOnly()
		{

			string headerString = _jasixIndex.HeaderLine;
			_writer.WriteLine("{" + headerString+"}");
		}

		
		public int ProcessQuery(IEnumerable<string> queryStrings, bool printHeader = false)
		{
			_writer.Write("{");
			if (printHeader)
			{
				string headerString = _jasixIndex.HeaderLine;
				_writer.Write(headerString + ",");
			}
			Utilities.PrintQuerySectionOpening(JasixCommons.SectionToIndex, _writer);

		    var count = 0;
		    foreach (string queryString in queryStrings)
            {
                var query = Utilities.ParseQuery(queryString);
                query.Chromosome = _jasixIndex.GetIndexChromName(query.Chromosome);
                if (!_jasixIndex.ContainsChr(query.Chromosome)) continue;

                count = PrintLargeVariantsExtendingIntoQuery(query);
                count += PrintAllVariantsFromQueryBegin(query, count > 0);
            }

            Utilities.PrintQuerySectionClosing(_writer);
			_writer.WriteLine("}");
		    return count;

		}

		private int PrintAllVariantsFromQueryBegin((string, int, int) query, bool needComma)
		{
		    var count = 0;
			foreach (string line in ReadOverlappingJsonLines(query))
			{
				Utilities.PrintJsonEntry(line, needComma, _writer);
				needComma = true;
			    count++;
			}

		    return count;
		}
		private int PrintLargeVariantsExtendingIntoQuery((string, int, int) query)
		{
		    var count = 0;
			foreach (string line in ReadJsonLinesExtendingInto(query))
			{
				Utilities.PrintJsonEntry(line, count>0, _writer);
			    count++;
			}

			return count;
		}

		internal IEnumerable<string> ReadJsonLinesExtendingInto((string Chr, int Start, int End) query)
		{
			// query for large variants like chr1:100-99 returns all overlapping large variants that start before 100
			var locations = _jasixIndex.LargeVariantPositions(query.Chr, query.Start, query.Start - 1);

			if (locations == null || locations.Length == 0) yield break;

			foreach (long location in locations)
			{
				RepositionReader(location);

				string line;
				if ((line = _jsonReader.ReadLine()) == null) continue;
				line = line.TrimEnd(',');

				yield return line;

			}
		}

		private void RepositionReader(long location)
		{
			_jsonReader.DiscardBufferedData();
			_jsonReader.BaseStream.Position = location;
		}

		internal IEnumerable<string> ReadOverlappingJsonLines((string Chr, int Start, int End) query)
		{
			long position = _jasixIndex.GetFirstVariantPosition(query.Chr, query.Start, query.End);

			if (position == -1) yield break;

			RepositionReader(position);

			string line;
			while ((line = _jsonReader.ReadLine()) != null && !line.OptimizedStartsWith(']'))
				//The array of positions entry end with "]," Going past it will cause the json parser to crash
			{
				line = line.TrimEnd(',');
                if (string.IsNullOrEmpty(line)) continue;
			    
				JsonSchema jsonEntry;
				try
				{
					jsonEntry = JsonConvert.DeserializeObject<JsonSchema>(line);
				}
				catch (Exception)
				{
					Console.WriteLine($"Error in line:\n{line}");
					throw;
				}

			    string jsonChrom = _jasixIndex.GetIndexChromName(jsonEntry.chromosome);
				if (jsonChrom != query.Chr) break;

				if (jsonEntry.Start > query.End) break;

				if (!jsonEntry.Overlaps(query.Start, query.End)) continue;
				// if there is an SV that starts before the query start that is printed by the large variant printer
				if (Utilities.IsLargeVariant(jsonEntry.Start, jsonEntry.End) && jsonEntry.Start < query.Start) continue;
				yield return line;
			}
		}


	    
	}
}
