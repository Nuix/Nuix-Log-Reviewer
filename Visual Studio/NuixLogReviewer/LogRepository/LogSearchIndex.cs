using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using NuixLogReviewerObjects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Class which handles interfacing with the Lucene search index (Lucene.NET 4.8).
    /// </summary>
    public class LogSearchIndex : IDisposable
    {
        // The Lucene version the index and analyzers are built against.
        internal const LuceneVersion Version = LuceneVersion.LUCENE_48;

        private static readonly Regex NotFixRegex = new Regex(@"\bnot\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly ConcurrentDictionary<string, Query> QueryCache = new ConcurrentDictionary<string, Query>();

        private readonly object _lockObject = new object();
        private readonly LogPatternMasker _masker = LogPatternMasker.CreateDefault();
        private Analyzer _analyzer;
        private FSDirectory _directory;
        private IndexWriter _writer;
        private DirectoryReader _reader;
        private IndexSearcher _searcher;
        private bool _disposed;

        public string IndexDirectory { get; private set; }
        public bool InWriteMode { get; private set; }

        public LogSearchIndex(string indexDirectory)
        {
            IndexDirectory = indexDirectory ?? throw new ArgumentNullException(nameof(indexDirectory));
            _analyzer = CreateAnalyzer();
            InitializeDirectory();
        }

        /// <summary>
        /// Builds the per-field analyzer. The keyword-style fields (level, channel, source) are
        /// indexed as a single lower-cased token so that searches are case-insensitive and match
        /// the whole field value; the classic QueryParser also runs query terms through this same
        /// analyzer, so query text for these fields is lower-cased to match. This is what allows
        /// queries like "level:error" and "flag:worker_log" to hit regardless of the casing used
        /// in the log. The content field uses StandardAnalyzer (tokenized, lower-cased) so free
        /// text search works, and flag uses whitespace tokenization since flags are space-joined.
        /// </summary>
        internal static Analyzer CreateAnalyzer()
        {
            var perField = new Dictionary<string, Analyzer>
            {
                ["level"] = new LowerCaseKeywordAnalyzer(),
                ["channel"] = new LowerCaseKeywordAnalyzer(),
                ["source"] = new LowerCaseKeywordAnalyzer(),
                ["flag"] = new WhitespaceAnalyzer(Version),
                ["exists"] = new KeywordAnalyzer(),
            };
            return new PerFieldAnalyzerWrapper(new StandardAnalyzer(Version), perField);
        }

        private void InitializeDirectory()
        {
            if (!System.IO.Directory.Exists(IndexDirectory))
                System.IO.Directory.CreateDirectory(IndexDirectory);
            _directory = FSDirectory.Open(IndexDirectory);
        }

        private IndexSearcher GetSearcher()
        {
            if (_reader == null)
            {
                _reader = DirectoryReader.Open(_directory);
                _searcher = new IndexSearcher(_reader);
            }
            else
            {
                // Reopen only if the index has changed since the reader was created.
                var refreshed = DirectoryReader.OpenIfChanged(_reader);
                if (refreshed != null)
                {
                    _reader.Dispose();
                    _reader = refreshed;
                    _searcher = new IndexSearcher(_reader);
                }
            }
            return _searcher;
        }

        public void BeginWrite()
        {
            lock (_lockObject)
            {
                if (InWriteMode) return;

                try
                {
                    _searcher = null;
                    _reader?.Dispose();
                    _reader = null;

                    var config = new IndexWriterConfig(Version, _analyzer)
                    {
                        OpenMode = OpenMode.CREATE_OR_APPEND,
                        RAMBufferSizeMB = 256,
                    };
                    _writer = new IndexWriter(_directory, config);
                    InWriteMode = true;
                }
                catch (LockObtainFailedException)
                {
                    throw new InvalidOperationException("Index is locked by another process");
                }
            }
        }

        public void EndWrite()
        {
            lock (_lockObject)
            {
                if (!InWriteMode) return;

                try
                {
                    _writer?.Commit();
                    _writer?.ForceMerge(1);
                }
                finally
                {
                    _writer?.Dispose();
                    _writer = null;
                    InWriteMode = false;
                }
            }

            // Anchor "age" queries to the newest event in the loaded set, and record the oldest for
            // the loaded-range display. Computed after the write completes so they reflect everything
            // just indexed, and kept stable regardless of when the search is run.
            ComputeTimestampBounds();

            // Age queries depend on ReferenceTicks, so any cached queries are now stale.
            QueryCache.Clear();
        }

        /// <summary>
        /// Reference time (event ticks) that the virtual "age" field measures back from - the newest
        /// event timestamp across the loaded set. Null when the index is empty.
        /// </summary>
        public long? ReferenceTicks { get; private set; }

        /// <summary>Oldest event timestamp (ticks) across the loaded set. Null when empty.</summary>
        public long? MinTimestampTicks { get; private set; }

        /// <summary>Newest event timestamp (ticks) across the loaded set. Alias of ReferenceTicks.</summary>
        public long? MaxTimestampTicks => ReferenceTicks;

        /// <summary>
        /// Computes the min and max indexed "timestamp" (ticks) using single-hit sorts (ascending and
        /// descending). Sets both to null if the index has no documents.
        /// </summary>
        private void ComputeTimestampBounds()
        {
            var searcher = GetSearcher();
            if (searcher.IndexReader.MaxDoc == 0)
            {
                MinTimestampTicks = null;
                ReferenceTicks = null;
                return;
            }

            MinTimestampTicks = TopTimestampTicks(searcher, reverse: false);
            ReferenceTicks = TopTimestampTicks(searcher, reverse: true);
        }

        private static long? TopTimestampTicks(IndexSearcher searcher, bool reverse)
        {
            var sort = new Sort(new SortField("timestamp", SortFieldType.INT64, reverse));
            var top = searcher.Search(new MatchAllDocsQuery(), 1, sort);
            if (top.ScoreDocs.Length == 0)
            {
                return null;
            }
            var fd = (FieldDoc)top.ScoreDocs[0];
            return Convert.ToInt64(fd.Fields[0]);
        }

        public void IndexLogEntry(NuixLogEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!InWriteMode) throw new InvalidOperationException("LogSearchIndex is not in write mode");

            var doc = new Document
            {
                new Int64Field("id", entry.ID, Field.Store.YES),
                new Int64Field("line", entry.LineNumber, Field.Store.NO),
                // TextField => analyzed by the per-field analyzer configured in CreateAnalyzer.
                new TextField("channel", entry.Channel ?? "", Field.Store.NO),
                new TextField("level", entry.Level ?? "", Field.Store.NO),
                new TextField("source", entry.Source ?? "", Field.Store.NO),
                new TextField("content", entry.Content ?? "", Field.Store.NO),
                // exists is a fixed marker token used by ParseQuery for pure-negation queries.
                new StringField("exists", "yes", Field.Store.NO),
                // Full timestamp (ticks) for precise sorting/range queries; date is day-granularity.
                new Int64Field("timestamp", entry.TimeStamp.Ticks, Field.Store.NO),
                new Int64Field("date",
                    entry.TimeStamp.Year * 10000 + entry.TimeStamp.Month * 100 + entry.TimeStamp.Day,
                    Field.Store.NO),
                // Doc-values columns for fast single-pass scans (time-series bucketing + the
                // per-search level/flag summary; see BucketedLevelCounts and SummarizeFilteredSet):
                // ts_dv = event ticks; lvl_dv = level code (0=INFO, 1=WARN, 2=ERROR, 3=DEBUG, 4=other).
                // The chart collectors only tally 0..2, so DEBUG/other are simply ignored there; the
                // summary pass uses 3 to report the DEBUG readout without a separate Lucene count.
                new NumericDocValuesField("ts_dv", entry.TimeStamp.Ticks),
                new NumericDocValuesField("lvl_dv", LevelCode(entry.Level)),
                // Pattern-mining columns (see MinePatterns): tmpl_dv = the normalized message template
                // (computed once here so the mining pass is a fast doc-values scan); id_dv mirrors the
                // entry id as a doc-value so the pass can bucket ids without stored-field retrieval.
                new SortedDocValuesField("tmpl_dv", new BytesRef(_masker.Mask(entry.Content))),
                new NumericDocValuesField("id_dv", entry.ID),
                // Raw content, stored, so the Patterns view can reveal a concrete example line on demand.
                new StoredField("content_raw", entry.Content ?? ""),
            };

            if (entry.Flags?.Any() == true)
            {
                doc.Add(new TextField("flag", string.Join(" ", entry.Flags), Field.Store.NO));
                // flag_dv: one multi-valued doc-value per flag, so the per-search summary can tally
                // every classifier flag for the matched set in a single columnar pass rather than one
                // Lucene count per flag (see SummarizeFilteredSet). Flags are already lower-cased.
                foreach (var flag in entry.Flags)
                {
                    if (!string.IsNullOrWhiteSpace(flag))
                        doc.Add(new SortedSetDocValuesField("flag_dv", new BytesRef(flag)));
                }
            }

            _writer.AddDocument(doc);
        }

        /// <summary>
        /// Maps a level string to the compact code used by the doc-values columns.
        /// 0=INFO, 1=WARN, 2=ERROR, 3=DEBUG, 4=other (e.g. TRACE). The chart collectors only look at
        /// 0..2; the per-search summary additionally reports 3 (DEBUG).
        /// </summary>
        private static int LevelCode(string level)
        {
            if (string.Equals(level, "INFO", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(level, "DEBUG", StringComparison.OrdinalIgnoreCase)) return 3;
            return 4;
        }

        public IList<long> Search(NuixLogRepo repo, string queryString, int maxResults = 100000)
        {
            if (string.IsNullOrWhiteSpace(queryString))
                return new AllEntriesIDList((int)repo.Database.TotalRecords);

            queryString = NotFixRegex.Replace(queryString, "NOT");
            var query = ParseQuery(queryString);
            var sort = new Sort(
                new SortField("timestamp", SortFieldType.INT64),
                new SortField("line", SortFieldType.INT64));

            var searcher = GetSearcher();
            var hits = searcher.Search(query, maxResults, sort);

            var ids = new long[hits.ScoreDocs.Length];
            for (int i = 0; i < hits.ScoreDocs.Length; i++)
            {
                var visitor = new DocumentStoredFieldVisitor("id");
                searcher.Doc(hits.ScoreDocs[i].Doc, visitor);
                ids[i] = long.Parse(visitor.Document.Get("id"));
            }

            return ids;
        }

        private Query ParseQuery(string queryString)
        {
            // Queries that reference the virtual "age" field depend on ReferenceTicks (which varies
            // per loaded set), so they must not be served from the shared static cache. Everything
            // else is safe to cache.
            bool ageDependent = queryString.IndexOf(CustomQueryParser.AgeField + ":", StringComparison.OrdinalIgnoreCase) >= 0;

            if (ageDependent)
            {
                return BuildQuery(queryString);
            }

            return QueryCache.GetOrAdd(queryString, BuildQuery);
        }

        private Query BuildQuery(string q)
        {
            var parser = new CustomQueryParser(Version, _analyzer)
            {
                DefaultOperator = Operator.AND,
                // Default field is set to "content" in the CustomQueryParser constructor.
                AgeReferenceTicks = ReferenceTicks,
            };
            parser.LongFields.Add("line");
            parser.LongFields.Add("id");
            parser.LongFields.Add("date");
            parser.LongFields.Add("timestamp");

            var query = parser.Parse(q);
            if (query is BooleanQuery bq && bq.Clauses.Count > 0 &&
                bq.Clauses.All(clause => clause.Occur == Occur.MUST_NOT))
            {
                // A query consisting only of negations matches nothing on its own. Anchor it to
                // the "exists" marker so it returns all documents that don't match. The anchor
                // MUST be a sibling of the negation clauses (a flat BooleanQuery); nesting the
                // negations inside a subclause (e.g. "exists:yes AND (NOT x)") would make the
                // subclause match nothing and yield zero results.
                var anchored = new BooleanQuery
                {
                    { new TermQuery(new Term("exists", "yes")), Occur.MUST }
                };
                foreach (var clause in bq.Clauses)
                {
                    anchored.Add(clause);
                }
                query = anchored;
            }
            return query;
        }

        public int Count(NuixLogRepo repo, string queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString))
                return (int)repo.Database.TotalRecords;

            queryString = NotFixRegex.Replace(queryString, "NOT");
            var query = ParseQuery(queryString);
            var searcher = GetSearcher();
            return searcher.Search(query, 1).TotalHits;
        }

        /// <summary>
        /// Per-search summary of a matched set: total hit count, per-level counts (INFO/WARN/ERROR/DEBUG)
        /// and per-flag counts, plus the time span. This is what the status-bar level readouts, the
        /// Classifiers table and the Range label all consume for a normal search.
        /// </summary>
        public sealed class FilteredSetSummary
        {
            public int Total { get; set; }
            public int Info { get; set; }
            public int Warn { get; set; }
            public int Error { get; set; }
            public int Debug { get; set; }
            /// <summary>Count of matched entries carrying each flag. Only flags present in the set appear.</summary>
            public Dictionary<string, int> FlagCounts { get; } = new Dictionary<string, int>();
            public long? MinTicks { get; set; }
            public long? MaxTicks { get; set; }
        }

        /// <summary>
        /// Computes the whole per-search summary (total, per-level and per-flag counts, time span) for
        /// the matched set in a SINGLE doc-values pass. This replaces what used to be a stack of
        /// separate Lucene searches per search run (four level counts + one count per classifier flag +
        /// a DISTINCT(Flags) DB scan), which was the dominant cost of running a broad query such as the
        /// blank "clear search". Reads lvl_dv/ts_dv (and the multi-valued flag_dv) only, so it scales
        /// with the match count, not the number of flags. A blank query covers the whole loaded set.
        /// </summary>
        public FilteredSetSummary SummarizeFilteredSet(string queryString)
        {
            Query query;
            if (string.IsNullOrWhiteSpace(queryString))
            {
                query = new MatchAllDocsQuery();
            }
            else
            {
                queryString = NotFixRegex.Replace(queryString, "NOT");
                query = ParseQuery(queryString);
            }

            var summary = new FilteredSetSummary();
            var searcher = GetSearcher();
            if (searcher.IndexReader.MaxDoc == 0)
            {
                return summary;
            }

            var collector = new FilteredSetSummaryCollector(summary);
            searcher.Search(query, collector);
            return summary;
        }

        /// <summary>
        /// Returns the oldest and newest event timestamps (ticks) among documents matching the query,
        /// or (null, null) when nothing matches. A blank query covers the whole loaded set. Uses two
        /// single-hit sorted searches, so it is cheap regardless of match count.
        /// </summary>
        public (long? MinTicks, long? MaxTicks) TimestampBoundsForQuery(string queryString)
        {
            Query query;
            if (string.IsNullOrWhiteSpace(queryString))
            {
                query = new MatchAllDocsQuery();
            }
            else
            {
                queryString = NotFixRegex.Replace(queryString, "NOT");
                query = ParseQuery(queryString);
            }

            var searcher = GetSearcher();
            if (searcher.IndexReader.MaxDoc == 0)
            {
                return (null, null);
            }

            long? min = TopTimestampTicksForQuery(searcher, query, reverse: false);
            long? max = TopTimestampTicksForQuery(searcher, query, reverse: true);
            return (min, max);
        }

        private static long? TopTimestampTicksForQuery(IndexSearcher searcher, Query query, bool reverse)
        {
            var sort = new Sort(new SortField("timestamp", SortFieldType.INT64, reverse));
            var top = searcher.Search(query, 1, sort);
            if (top.ScoreDocs.Length == 0)
            {
                return null;
            }
            var fd = (FieldDoc)top.ScoreDocs[0];
            return Convert.ToInt64(fd.Fields[0]);
        }

        /// <summary>
        /// Result of a time-series bucketing pass: per-bucket INFO/WARN/ERROR counts plus the time
        /// window (ticks) the buckets span. <see cref="Counts"/> is [bucket, level] where level is
        /// 0=INFO, 1=WARN, 2=ERROR. When the matched set is empty, Counts is empty and the ticks are null.
        /// </summary>
        public sealed class TimeSeries
        {
            public int[,] Counts { get; set; }
            public int Buckets { get; set; }
            public long? MinTicks { get; set; }
            public long? MaxTicks { get; set; }
            public bool IsEmpty => MinTicks == null || MaxTicks == null;
        }

        /// <summary>
        /// Buckets the matched set into <paramref name="buckets"/> equal time slices over the matched
        /// set's own min..max span, counting INFO/WARN/ERROR per slice. Single pass over matching docs
        /// via doc-values (ts_dv, lvl_dv), so it is fast and scales with the match count, not the index.
        /// </summary>
        public TimeSeries BucketedLevelCounts(string queryString, int buckets)
        {
            if (buckets < 1) buckets = 1;

            Query query;
            if (string.IsNullOrWhiteSpace(queryString))
            {
                query = new MatchAllDocsQuery();
            }
            else
            {
                queryString = NotFixRegex.Replace(queryString, "NOT");
                query = ParseQuery(queryString);
            }

            var searcher = GetSearcher();

            // Establish the time window from the matched set itself.
            long? min = TopTimestampTicksForQuery(searcher, query, reverse: false);
            long? max = TopTimestampTicksForQuery(searcher, query, reverse: true);
            if (min == null || max == null)
            {
                return new TimeSeries { Counts = new int[0, 3], Buckets = buckets, MinTicks = null, MaxTicks = null };
            }

            long span = Math.Max(1, max.Value - min.Value);
            var counts = new int[buckets, 3];
            var collector = new LevelBucketCollector(min.Value, span, buckets, counts);
            searcher.Search(query, collector);

            return new TimeSeries { Counts = counts, Buckets = buckets, MinTicks = min, MaxTicks = max };
        }

        /// <summary>
        /// One row of the Patterns view: a normalized message template, how many entries produced it,
        /// the time span over which it occurred, and the ids of the contributing entries (for drill-down).
        /// </summary>
        public sealed class LogPattern
        {
            public string Template { get; set; }
            public int Count { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            /// <summary>Ids of the entries in this pattern bucket, for exact drill-down selection.</summary>
            public IReadOnlyList<long> Ids { get; set; }
        }

        /// <summary>
        /// Groups the matched set by normalized message template and returns each template with its
        /// count, first/last-seen times, and the contributing entry ids. A blank query mines the whole
        /// loaded set. Single pass over matching docs via doc-values (tmpl_dv/ts_dv/id_dv), so it scales
        /// with the match count. Results are ordered by count descending.
        /// </summary>
        public IList<LogPattern> MinePatterns(string queryString)
        {
            Query query;
            if (string.IsNullOrWhiteSpace(queryString))
            {
                query = new MatchAllDocsQuery();
            }
            else
            {
                queryString = NotFixRegex.Replace(queryString, "NOT");
                query = ParseQuery(queryString);
            }

            var searcher = GetSearcher();
            var collector = new PatternCollector();
            searcher.Search(query, collector);

            return collector.Build();
        }

        /// <summary>
        /// Summary of a set of entries identified by id: per-level counts and the time span. Used by
        /// the Patterns view drill-down so the level readouts, range and chart stay consistent when the
        /// grid is populated from an explicit id set rather than a query.
        /// </summary>
        public sealed class IdSetSummary
        {
            public int Info { get; set; }
            public int Warn { get; set; }
            public int Error { get; set; }
            public int Debug { get; set; }
            public long? MinTicks { get; set; }
            public long? MaxTicks { get; set; }
        }

        /// <summary>
        /// Single pass over the whole index reading id_dv/lvl_dv/ts_dv, tallying only the documents whose
        /// id is in <paramref name="ids"/>. Per-level counts (INFO/WARN/ERROR/DEBUG) come from lvl_dv;
        /// the grid still shows every entry regardless of level.
        /// </summary>
        public IdSetSummary SummarizeIds(ICollection<long> ids)
        {
            var summary = new IdSetSummary();
            if (ids == null || ids.Count == 0) return summary;

            var idSet = ids as HashSet<long> ?? new HashSet<long>(ids);
            var searcher = GetSearcher();
            var collector = new IdSetSummaryCollector(idSet, summary);
            searcher.Search(new MatchAllDocsQuery(), collector);
            return summary;
        }

        /// <summary>
        /// Builds the time-series (bucketed INFO/WARN/ERROR counts) for an explicit id set, over the
        /// set's own min..max span. Used by the Patterns view drill-down so the chart shows the real
        /// distribution of the selected pattern's entries. Two single passes over the index (bounds via
        /// the summary, then bucketing); fine for on-demand drill-down.
        /// </summary>
        public TimeSeries BucketedLevelCountsForIds(ICollection<long> ids, int buckets)
        {
            if (buckets < 1) buckets = 1;

            var summary = SummarizeIds(ids);
            if (summary.MinTicks == null || summary.MaxTicks == null)
            {
                return new TimeSeries { Counts = new int[buckets, 3], Buckets = buckets, MinTicks = null, MaxTicks = null };
            }

            long min = summary.MinTicks.Value;
            long max = summary.MaxTicks.Value;
            long span = Math.Max(1, max - min);
            var counts = new int[buckets, 3];

            var idSet = ids as HashSet<long> ?? new HashSet<long>(ids);
            var searcher = GetSearcher();
            var collector = new IdSetLevelBucketCollector(idSet, min, span, buckets, counts);
            searcher.Search(new MatchAllDocsQuery(), collector);

            return new TimeSeries { Counts = counts, Buckets = buckets, MinTicks = min, MaxTicks = max };
        }

        public void Dispose()
        {
            if (_disposed) return;

            EndWrite();
            _reader?.Dispose();
            _directory?.Dispose();
            _analyzer?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Emits the entire field value as a single, lower-cased token. Used for keyword-style fields
    /// (level, channel, source) so that matching is exact-per-value but case-insensitive.
    /// </summary>
    internal sealed class LowerCaseKeywordAnalyzer : Analyzer
    {
        protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
        {
            var tokenizer = new KeywordTokenizer(reader);
            TokenStream stream = new LowerCaseFilter(LogSearchIndex.Version, tokenizer);
            return new TokenStreamComponents(tokenizer, stream);
        }
    }

    /// <summary>
    /// Collector that reads the ts_dv (event ticks) and lvl_dv (level code) doc-values for each
    /// matching document and increments the corresponding [bucket, level] cell. Only INFO/WARN/ERROR
    /// (codes 0/1/2) are tallied; other levels are ignored for the chart.
    /// </summary>
    internal sealed class LevelBucketCollector : ICollector
    {
        private readonly long _min;
        private readonly long _span;
        private readonly int _buckets;
        private readonly int[,] _counts;
        private NumericDocValues _ts;
        private NumericDocValues _lvl;

        public LevelBucketCollector(long min, long span, int buckets, int[,] counts)
        {
            _min = min;
            _span = span < 1 ? 1 : span;
            _buckets = buckets;
            _counts = counts;
        }

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            _lvl = context.AtomicReader.GetNumericDocValues("lvl_dv");
        }

        public void Collect(int doc)
        {
            if (_ts == null || _lvl == null) return;

            long ticks = _ts.Get(doc);
            int b = (int)((ticks - _min) * (_buckets - 1) / _span);
            if (b < 0) b = 0;
            else if (b >= _buckets) b = _buckets - 1;

            int lvl = (int)_lvl.Get(doc);
            if (lvl >= 0 && lvl <= 2)
            {
                _counts[b, lvl]++;
            }
        }

        public bool AcceptsDocsOutOfOrder => true;
    }

    /// <summary>
    /// Collector that groups matching documents by their normalized message template (tmpl_dv),
    /// accumulating per-template count, first/last-seen (ts_dv) and the contributing ids (id_dv).
    /// Reads only doc-values, so no per-document stored-field retrieval is needed - the whole pass
    /// is a fast columnar scan.
    /// </summary>
    internal sealed class PatternCollector : ICollector
    {
        private sealed class Bucket
        {
            public List<long> Ids = new List<long>();
            public long FirstTicks;
            public long LastTicks;
        }

        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>(4096);
        private readonly BytesRef _scratch = new BytesRef();
        private SortedDocValues _tmpl;
        private NumericDocValues _ts;
        private NumericDocValues _id;

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _tmpl = context.AtomicReader.GetSortedDocValues("tmpl_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            _id = context.AtomicReader.GetNumericDocValues("id_dv");
        }

        public void Collect(int doc)
        {
            if (_tmpl == null || _ts == null || _id == null) return;

            _tmpl.Get(doc, _scratch);
            string template = _scratch.Utf8ToString();
            long ticks = _ts.Get(doc);
            long id = _id.Get(doc);

            if (!_buckets.TryGetValue(template, out var bucket))
            {
                bucket = new Bucket { FirstTicks = ticks, LastTicks = ticks };
                _buckets[template] = bucket;
            }
            bucket.Ids.Add(id);
            if (ticks < bucket.FirstTicks) bucket.FirstTicks = ticks;
            if (ticks > bucket.LastTicks) bucket.LastTicks = ticks;
        }

        public bool AcceptsDocsOutOfOrder => true;

        /// <summary>Materializes the accumulated buckets into result rows, ordered by count descending.</summary>
        public IList<LogSearchIndex.LogPattern> Build()
        {
            var result = new List<LogSearchIndex.LogPattern>(_buckets.Count);
            foreach (var kv in _buckets)
            {
                result.Add(new LogSearchIndex.LogPattern
                {
                    Template = kv.Key,
                    Count = kv.Value.Ids.Count,
                    FirstSeen = new DateTime(kv.Value.FirstTicks),
                    LastSeen = new DateTime(kv.Value.LastTicks),
                    Ids = kv.Value.Ids,
                });
            }
            result.Sort((a, b) => b.Count.CompareTo(a.Count));
            return result;
        }
    }

    /// <summary>
    /// Collector that tallies per-level counts and min/max event ticks for the subset of documents
    /// whose id_dv is contained in the supplied id set. Reads only doc-values.
    /// </summary>
    internal sealed class IdSetSummaryCollector : ICollector
    {
        private readonly HashSet<long> _ids;
        private readonly LogSearchIndex.IdSetSummary _summary;
        private NumericDocValues _id;
        private NumericDocValues _lvl;
        private NumericDocValues _ts;

        public IdSetSummaryCollector(HashSet<long> ids, LogSearchIndex.IdSetSummary summary)
        {
            _ids = ids;
            _summary = summary;
        }

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _id = context.AtomicReader.GetNumericDocValues("id_dv");
            _lvl = context.AtomicReader.GetNumericDocValues("lvl_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
        }

        public void Collect(int doc)
        {
            if (_id == null) return;
            long id = _id.Get(doc);
            if (!_ids.Contains(id)) return;

            switch ((int)(_lvl?.Get(doc) ?? 4))
            {
                case 0: _summary.Info++; break;
                case 1: _summary.Warn++; break;
                case 2: _summary.Error++; break;
                case 3: _summary.Debug++; break;
            }

            long ticks = _ts?.Get(doc) ?? 0;
            if (_summary.MinTicks == null || ticks < _summary.MinTicks) _summary.MinTicks = ticks;
            if (_summary.MaxTicks == null || ticks > _summary.MaxTicks) _summary.MaxTicks = ticks;
        }

        public bool AcceptsDocsOutOfOrder => true;
    }

    /// <summary>
    /// Collector that buckets the INFO/WARN/ERROR counts by time (like <see cref="LevelBucketCollector"/>)
    /// but only for documents whose id_dv is in the supplied id set. Used for the Patterns drill-down chart.
    /// </summary>
    internal sealed class IdSetLevelBucketCollector : ICollector
    {
        private readonly HashSet<long> _ids;
        private readonly long _min;
        private readonly long _span;
        private readonly int _buckets;
        private readonly int[,] _counts;
        private NumericDocValues _id;
        private NumericDocValues _ts;
        private NumericDocValues _lvl;

        public IdSetLevelBucketCollector(HashSet<long> ids, long min, long span, int buckets, int[,] counts)
        {
            _ids = ids;
            _min = min;
            _span = span < 1 ? 1 : span;
            _buckets = buckets;
            _counts = counts;
        }

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _id = context.AtomicReader.GetNumericDocValues("id_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            _lvl = context.AtomicReader.GetNumericDocValues("lvl_dv");
        }

        public void Collect(int doc)
        {
            if (_id == null || _ts == null || _lvl == null) return;
            long id = _id.Get(doc);
            if (!_ids.Contains(id)) return;

            long ticks = _ts.Get(doc);
            int b = (int)((ticks - _min) * (_buckets - 1) / _span);
            if (b < 0) b = 0;
            else if (b >= _buckets) b = _buckets - 1;

            int lvl = (int)_lvl.Get(doc);
            if (lvl >= 0 && lvl <= 2)
            {
                _counts[b, lvl]++;
            }
        }

        public bool AcceptsDocsOutOfOrder => true;
    }

    /// <summary>
    /// Collector that computes a whole per-search summary in one pass: total hits, per-level counts
    /// (INFO/WARN/ERROR/DEBUG from lvl_dv), per-flag counts (from the multi-valued flag_dv) and the
    /// event-time span (ts_dv). Reads only doc-values. This collapses what used to be many separate
    /// Lucene count searches (one per level + one per flag) into a single columnar scan.
    /// </summary>
    internal sealed class FilteredSetSummaryCollector : ICollector
    {
        private readonly LogSearchIndex.FilteredSetSummary _summary;
        private readonly BytesRef _scratch = new BytesRef();
        private NumericDocValues _lvl;
        private NumericDocValues _ts;
        private SortedSetDocValues _flags;

        public FilteredSetSummaryCollector(LogSearchIndex.FilteredSetSummary summary)
        {
            _summary = summary;
        }

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _lvl = context.AtomicReader.GetNumericDocValues("lvl_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            // flag_dv may be absent for a segment where no entry carried a flag.
            _flags = context.AtomicReader.GetSortedSetDocValues("flag_dv");
        }

        public void Collect(int doc)
        {
            _summary.Total++;

            switch ((int)(_lvl?.Get(doc) ?? 4))
            {
                case 0: _summary.Info++; break;
                case 1: _summary.Warn++; break;
                case 2: _summary.Error++; break;
                case 3: _summary.Debug++; break;
            }

            if (_ts != null)
            {
                long ticks = _ts.Get(doc);
                if (_summary.MinTicks == null || ticks < _summary.MinTicks) _summary.MinTicks = ticks;
                if (_summary.MaxTicks == null || ticks > _summary.MaxTicks) _summary.MaxTicks = ticks;
            }

            if (_flags != null)
            {
                _flags.SetDocument(doc);
                long ord;
                while ((ord = _flags.NextOrd()) != SortedSetDocValues.NO_MORE_ORDS)
                {
                    _flags.LookupOrd(ord, _scratch);
                    string flag = _scratch.Utf8ToString();
                    _summary.FlagCounts.TryGetValue(flag, out int c);
                    _summary.FlagCounts[flag] = c + 1;
                }
            }
        }

        public bool AcceptsDocsOutOfOrder => true;
    }
}
