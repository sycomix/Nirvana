﻿using System;
using System.Collections.Generic;
using System.IO;
using IO;
using VariantAnnotation;
using CommandLine.Utilities;
using VariantAnnotation.GeneAnnotation;
using VariantAnnotation.Interface;
using VariantAnnotation.Interface.GeneAnnotation;
using VariantAnnotation.Interface.Plugins;
using VariantAnnotation.Interface.Providers;
using VariantAnnotation.Interface.SA;
using VariantAnnotation.NSA;
using VariantAnnotation.Providers;
using VariantAnnotation.SA;

namespace Nirvana
{
    public static class ProviderUtilities
    {
        public static IAnnotator GetAnnotator(IAnnotationProvider taProvider, ISequenceProvider sequenceProvider,
            IAnnotationProvider saProviders, IAnnotationProvider conservationProvider,
            IGeneAnnotationProvider geneAnnotationProviders, IEnumerable<IPlugin> plugins = null)
        {
            return new Annotator(taProvider, sequenceProvider, saProviders, conservationProvider,
                geneAnnotationProviders, plugins);
        }

        public static ISequenceProvider GetSequenceProvider(string compressedReferencePath)
        {
             return new ReferenceSequenceProvider(PersistentStreamUtils.GetReadStream(compressedReferencePath));
        }

        public static IAnnotationProvider GetConservationProvider(IEnumerable<(string dataFile, string indexFile)> dataAndIndexFiles)
        {
            if (dataAndIndexFiles == null) return null;

            foreach ((string dataFile, string indexFile) in dataAndIndexFiles)
            {
                if (dataFile.EndsWith(SaCommon.PhylopFileSuffix))
                    return new ConservationScoreProvider(PersistentStreamUtils.GetReadStream(dataFile), PersistentStreamUtils.GetReadStream(indexFile));
            }

            return null;
        }

        public static IRefMinorProvider GetRefMinorProvider(IEnumerable<(string dataFile, string indexFile)> dataAndIndexFiles)
        {
            if (dataAndIndexFiles == null) return null;

            foreach ((string dataFile, string indexFile) in dataAndIndexFiles)
            {
                if (dataFile.EndsWith(SaCommon.RefMinorFileSuffix))
                    return new RefMinorProvider(PersistentStreamUtils.GetReadStream(dataFile), PersistentStreamUtils.GetReadStream(indexFile));
            }

            return null;
        }

        public static IGeneAnnotationProvider GetGeneAnnotationProvider(IEnumerable<(string dataFile, string indexFile)> dataAndIndexFiles)
        {
            if (dataAndIndexFiles == null) return null;
            var ngaFiles = new List<string>();
            foreach ((string dataFile, string _) in dataAndIndexFiles)
            {
                if (dataFile.EndsWith(SaCommon.NgaFileSuffix))
                    ngaFiles.Add(dataFile);
            }
            return ngaFiles.Count > 0? new GeneAnnotationProvider(PersistentStreamUtils.GetStreams(ngaFiles)): null;
        }

        public static IAnnotationProvider GetNsaProvider(IEnumerable<(string dataFile, string indexFile)> dataAndIndexFiles)
        {
            if (dataAndIndexFiles == null) return null;

            var nsaReaders = new List<INsaReader>();
            var nsiReaders = new List<INsiReader>();
            foreach ((string dataFile, string indexFile)in dataAndIndexFiles)
            {
                if(dataFile.EndsWith(SaCommon.SaFileSuffix))
                    nsaReaders.Add(GetNsaReader(PersistentStreamUtils.GetReadStream(dataFile), PersistentStreamUtils.GetReadStream(indexFile)));
                if (dataFile.EndsWith(SaCommon.SiFileSuffix))
                    nsiReaders.Add(GetNsiReader(PersistentStreamUtils.GetReadStream(dataFile)));
            }

            if (nsaReaders.Count > 0 || nsiReaders.Count > 0)
                return new NsaProvider(nsaReaders.ToArray(), nsiReaders.ToArray());
            return null;
        }

        public static IList<(string dataFile, string indexFile)> GetSaDataAndIndexPaths(string saDirectoryPath)
        {
            var paths = new List<(string, string)>();
            if (Directory.Exists(saDirectoryPath))
            {
                foreach (var filePath in Directory.GetFiles(saDirectoryPath))
                {
                    if(filePath.EndsWith(SaCommon.SiFileSuffix) || filePath.EndsWith(SaCommon.NgaFileSuffix))
                        paths.Add((filePath, null));
                    else
                        paths.Add((filePath, filePath+SaCommon.IndexSufix));

                }

                return paths;
            }
            
            //if this is the saManifest url
            using (var reader = new StreamReader(PersistentStreamUtils.GetReadStream(saDirectoryPath)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    paths.Add((line, line+SaCommon.IndexSufix));
                }
            }


            return paths;
        }

        public static ITranscriptAnnotationProvider GetTranscriptAnnotationProvider(string path,
            ISequenceProvider sequenceProvider)
         {
            var benchmark = new Benchmark();
            var provider = new TranscriptAnnotationProvider(path, sequenceProvider);
            var wallTimeSpan = benchmark.GetElapsedTime();
            Console.WriteLine("Cache Time: {0} ms", wallTimeSpan.TotalMilliseconds);
            return provider;
        }

        
        private static NsaReader GetNsaReader(Stream dataStream, Stream indexStream) =>
            new NsaReader(new ExtendedBinaryReader(dataStream), indexStream);

        private static NsiReader GetNsiReader(Stream stream) => new NsiReader(stream);
    }
}