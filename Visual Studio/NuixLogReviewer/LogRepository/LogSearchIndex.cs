using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using NuixLogReviewerObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Class which handles interfacing with the Lucene search index
    /// </summary>
    public class LogSearchIndex
    {
        private PerFieldAnalyzerWrapper analyzer = null;
        private FSDirectory luceneDirectory;
        private IndexWriter writer;

        public string IndexDirectory { get; private set; }
        public bool InWriteMode { get; private set; }

        public LogSearchIndex(string indexDirectory)
        {
            IndexDirectory = indexDirectory;
            InWriteMode = false;
        }

        /// <summary>
        /// Gets the customized Analyzer used for indexing and searching.
        /// </summary>
        /// <returns>Customized analyzer</returns>
        private PerFieldAnalyzerWrapper getAnalyzer()
        {
            PerFieldAnalyzerWrapper analyzer = new PerFieldAnalyzerWrapper(new StandardAnalyzer(Lucene.Net.Util.Version.LUCENE_30));
            
            // Use WhitespaceAnalyzer for flags field so underscored terms are not split apart
            analyzer.AddAnalyzer("flag", new WhitespaceAnalyzer());

            return analyzer;
        }

        /// <summary>
        /// Begins writing by opening relevant Lucene objects and setting InWriteMode
        /// </summary>
        public void BeginWrite()
        {
            if (InWriteMode)
            {
                return;
            }
            else
            {
                analyzer = getAnalyzer();
                luceneDirectory = FSDirectory.Open(IndexDirectory);
                writer = new IndexWriter(luceneDirectory, analyzer, IndexWriter.MaxFieldLength.UNLIMITED);
                writer.MergeFactor = 30;
                writer.SetRAMBufferSizeMB(128);

                InWriteMode = true;
            }
        }

        /// <summary>
        /// Ends InWriteMode after closing Lucene index writing related objects.
        /// </summary>
        public void EndWrite()
        {
            writer.Flush(true, true, true);
            writer.Optimize(true);
            writer.Dispose();
            luceneDirectory.Dispose();
            analyzer.Dispose();
            InWriteMode = false;
        }

        /// <summary>
        /// Adds a document to the Lucene index, assuming we are currently InWriteMode (see BeginWrite())
        /// </summary>
        /// <param name="entry">The log entry to index, should already have corresponding DB ID in NuixLogEntry.ID</param>
        public void IndexLogEntry(NuixLogEntry entry)
        {
            if (!InWriteMode)
            {
                throw new Exception("LogSearchIndex is not currently in write mode!");
            }
            else
            {
                NumericField idField = new NumericField("id", Field.Store.YES, true);
                NumericField lineField = new NumericField("line");
                Field channelField = new Field("channel", "", Field.Store.NO, Field.Index.ANALYZED_NO_NORMS);
                Field levelField = new Field("level", "", Field.Store.NO, Field.Index.ANALYZED);
                Field sourceField = new Field("source", "", Field.Store.NO, Field.Index.ANALYZED_NO_NORMS);
                Field contentField = new Field("content", "", Field.Store.NO, Field.Index.ANALYZED);
                Field existsFields = new Field("exists", "yes", Field.Store.NO, Field.Index.ANALYZED_NO_NORMS);
                NumericField dateField = new NumericField("date");
                Field flagsField = new Field("flag", "", Field.Store.NO, Field.Index.ANALYZED);

                Document doc = new Document();
                doc.Add(existsFields);
                doc.Add(idField);
                doc.Add(lineField);
                doc.Add(channelField);
                doc.Add(levelField);
                doc.Add(sourceField);
                doc.Add(contentField);
                doc.Add(dateField);
                doc.Add(flagsField);

                idField.SetLongValue(entry.ID);
                lineField.SetLongValue(entry.LineNumber);
                channelField.SetValue(entry.Channel);
                levelField.SetValue(entry.Level);
                sourceField.SetValue(entry.Source);
                contentField.SetValue(entry.Content);
                //string dateString = entry.TimeStamp.ToString("yyyyMMdd");
                //long date = long.Parse(dateString);
                long date = (entry.TimeStamp.Year * 10000) + (entry.TimeStamp.Month * 100) + entry.TimeStamp.Day;
                dateField.SetLongValue(date);
                flagsField.SetValue(String.Join(" ", entry.Flags));
                writer.AddDocument(doc);
            }
        }

        public IList<long> Search(NuixLogRepo repo, string queryString)
        {
            // Lucene seems to be picky about lower case for some things?
            Regex notFix = new Regex(@"\bnot\b", RegexOptions.Compiled);
            queryString = notFix.Replace(queryString, "NOT");

            // Emtpy query is all items, we return a special collection that pretends to be a full list of ID values
            // but that is actually just based on the range of possible values.
            if (string.IsNullOrWhiteSpace(queryString))
            {
                return new AllEntriesIDList((int)repo.Database.TotalRecords);
            }
            else
            {
                luceneDirectory = FSDirectory.Open(IndexDirectory);
                analyzer = getAnalyzer();
                IndexSearcher searcher = new IndexSearcher(luceneDirectory);
                Query query = parseQuery(queryString);

                var hitsFound = searcher.Search(query, int.MaxValue);

                long[] ids = new long[hitsFound.TotalHits];
                for (int i = 0; i < hitsFound.TotalHits; i++)
                {
                    Document doc = searcher.Doc(hitsFound.ScoreDocs[i].Doc);
                    ids[i] = long.Parse(doc.Get("id"));
                }
                luceneDirectory.Dispose();
                searcher.Dispose();
                analyzer.Dispose();

                return ids;
            }
        }

        private Query parseQuery(string queryString)
        {
            CustomQueryParser parser = new CustomQueryParser(analyzer);
            parser.DefaultOperator = QueryParser.Operator.AND;
            parser.LongFields.Add("line");
            parser.LongFields.Add("id");
            parser.LongFields.Add("date");

            // Allows for negated query
            Query query = parser.Parse(queryString);
            if (query is BooleanQuery)
            {
                var bq = query as BooleanQuery;
                if (bq.Clauses.Count == bq.Clauses.Count(clause => clause.Occur == Occur.MUST_NOT))
                {
                    query = parser.Parse(string.Format("exists:yes AND ({0})", queryString));
                }
            }

            return query;
        }

        public int Count(NuixLogRepo repo, string queryString)
        {
            // Lucene seems to be picky about lower case for some things?
            Regex notFix = new Regex(@"\bnot\b", RegexOptions.Compiled);
            queryString = notFix.Replace(queryString, "NOT");

            // Blank query means all items
            if (string.IsNullOrWhiteSpace(queryString))
            {
                return (int)repo.Database.TotalRecords;
            }
            else
            {
                luceneDirectory = FSDirectory.Open(IndexDirectory);
                analyzer = getAnalyzer();
                IndexSearcher searcher = new IndexSearcher(luceneDirectory);

                Query query = parseQuery(queryString);

                var hitsFound = searcher.Search(query, int.MaxValue);
                int hitCount = hitsFound.TotalHits;

                luceneDirectory.Dispose();
                searcher.Dispose();
                analyzer.Dispose();

                return hitCount;
            }
        }
    }
}
