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

        // Second-stage template folding (Drain): maps each regex-template (tmpl_dv value) to a collapsed
        // "drain" template, so near-duplicate regex-templates group into one pattern. Built lazily from
        // the distinct tmpl_dv set on first use after a load, and invalidated whenever the reader is
        // (re)opened. When disabled/empty, patterns fall back to raw regex-templates (identity).
        private readonly bool _drainEnabled = true;
        private readonly double _drainSimilarity = 0.6;
        private IReadOnlyDictionary<string, string> _drainMap;             // regexTemplate -> drainTemplate
        private IReadOnlyDictionary<string, List<string>> _drainMembers;   // drainTemplate -> [regexTemplate...]

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
                ["job"] = new LowerCaseKeywordAnalyzer(),
                ["file"] = new LowerCaseKeywordAnalyzer(),
                ["tmpl"] = new LowerCaseKeywordAnalyzer(),
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
                _drainMap = null; _drainMembers = null; // rebuild against the new reader on demand
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
                    _drainMap = null; _drainMembers = null; // stale after content change
                }
            }
            return _searcher;
        }

        /// <summary>
        /// Runs a search that can be cancelled mid-scan: wraps the collector so it checks the token
        /// periodically and throws <see cref="OperationCanceledException"/> to unwind the Lucene scan.
        /// This is what lets a pathological/expensive query (e.g. an accidental huge range) be stopped
        /// by the Cancel button rather than running to completion. A default/none token is a no-op wrap.
        /// </summary>
        private static void SearchCancellable(IndexSearcher searcher, Query query, ICollector collector,
            System.Threading.CancellationToken cancel)
        {
            if (cancel.CanBeCanceled)
            {
                searcher.Search(query, new CancellableCollector(collector, cancel));
            }
            else
            {
                searcher.Search(query, collector);
            }
        }

        /// <summary>
        /// Collector decorator that throws <see cref="OperationCanceledException"/> when a cancellation
        /// token trips. Checks every N documents (cheap) plus on each new segment, so a long scan stops
        /// promptly without adding meaningful per-doc overhead.
        /// </summary>
        private sealed class CancellableCollector : ICollector
        {
            private const int CheckEvery = 4096;
            private readonly ICollector _inner;
            private readonly System.Threading.CancellationToken _cancel;
            private int _since;

            public CancellableCollector(ICollector inner, System.Threading.CancellationToken cancel)
            {
                _inner = inner;
                _cancel = cancel;
            }

            public void SetScorer(Scorer scorer) => _inner.SetScorer(scorer);

            public void SetNextReader(AtomicReaderContext context)
            {
                _cancel.ThrowIfCancellationRequested();
                _inner.SetNextReader(context);
            }

            public void Collect(int doc)
            {
                if (++_since >= CheckEvery)
                {
                    _since = 0;
                    _cancel.ThrowIfCancellationRequested();
                }
                _inner.Collect(doc);
            }

            public bool AcceptsDocsOutOfOrder => _inner.AcceptsDocsOutOfOrder;
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

            // Masked message template, computed once and reused for tmpl_dv (mining) and the queryable
            // tmpl keyword field (per-pattern hide/drill-down).
            string _maskedTemplate = _masker.Mask(entry.Content) ?? "";

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
                new SortedDocValuesField("tmpl_dv", new BytesRef(_maskedTemplate)),
                new NumericDocValuesField("id_dv", entry.ID),
                // line_dv mirrors the line number as a doc-value so the fast id-collection path (Search)
                // can reproduce the (timestamp, line) grid ordering without stored-field retrieval.
                new NumericDocValuesField("line_dv", entry.LineNumber),
                // Raw content, stored, so the Patterns view can reveal a concrete example line on demand.
                new StoredField("content_raw", entry.Content ?? ""),
            };

            // Queryable keyword form of the template (whole lowercased value as one token) so per-pattern
            // hide filters (AND NOT (tmpl:"<template>")) and pattern drill-down compose like flag:/file:.
            if (_maskedTemplate.Length > 0)
            {
                doc.Add(new TextField("tmpl", _maskedTemplate.ToLowerInvariant(), Field.Store.NO));
            }

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

            // Worker job association. The job id (job-<32hex>) is the job-level folder in the entry's
            // path; worker/restart subfolders below it don't change the id. Indexed two ways:
            //   - "job": a searchable keyword field so drill-down queries like job:job-<id> work;
            //   - "job_dv": a SortedDocValues column so JobCollector can group entries by job and
            //     compute per-job lifespans in a single fast columnar pass (like tmpl_dv for patterns).
            // Non-worker entries carry neither, so they're naturally excluded from job analysis.
            string jobId = JobIdExtractor.Extract(entry.FilePath);
            if (jobId != null)
            {
                doc.Add(new TextField("job", jobId, Field.Store.NO));
                doc.Add(new SortedDocValuesField("job_dv", new BytesRef(jobId)));
            }

            // Source file association. Indexed two ways (like job):
            //   - "file": a searchable keyword field (whole lowercased full path as one token) so
            //     drill-down queries like file:"<path>" and hide filters AND NOT (file:"<path>") work;
            //   - "file_dv": a single-valued SortedDocValues column so the per-search summary can tally
            //     per-file entry counts in the same columnar pass as flags (see FilteredSetSummary).
            // The full path is the identity (many worker logs are all named nuix.log). Lower-cased so
            // the doc-value key matches what the keyword analyzer produces for queries.
            string fileKey = (entry.FilePath ?? "").ToLowerInvariant();
            if (fileKey.Length > 0)
            {
                doc.Add(new TextField("file", fileKey, Field.Store.NO));
                doc.Add(new SortedDocValuesField("file_dv", new BytesRef(fileKey)));
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

        public IList<long> Search(NuixLogRepo repo, string queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString))
                // All entries, ordered by event time (ascending) so the grid's global order is truly
                // chronological. Using a materialized time-sorted id list (rather than the old
                // AllEntriesIDList position=>id+1 shortcut) is essential once MULTIPLE files are loaded:
                // ids are assigned in file/load order, which is NOT the same as time order, so the
                // shortcut mislocated an id's display row (e.g. a timeline click that resolved to a
                // worker-log event scrolled to an unrelated engine-log row at the id's numeric position).
                // A real time-ordered list makes both the grid order and IndexOfEntryId correct.
                return repo.Database.GetAllIds();

            queryString = NotFixRegex.Replace(queryString, "NOT");
            return SearchIds(ParseQuery(queryString));
        }

        /// <summary>
        /// Search variant that applies hidden templates as a fast <see cref="FieldCacheTermsFilter"/>
        /// MUST_NOT (see <see cref="ComposeFilteredQuery"/>) rather than a giant OR'd phrase negation in
        /// the query string. When there are no hidden templates and the base query is blank, uses the
        /// same time-sorted GetAllIds fast path as <see cref="Search(NuixLogRepo,string)"/>.
        /// </summary>
        public IList<long> Search(NuixLogRepo repo, string baseQuery, IReadOnlyCollection<string> hiddenTemplates,
            System.Threading.CancellationToken cancel = default)
        {
            if ((hiddenTemplates == null || hiddenTemplates.Count == 0) && string.IsNullOrWhiteSpace(baseQuery))
            {
                return repo.Database.GetAllIds();
            }
            return SearchIds(ComposeFilteredQuery(baseQuery, hiddenTemplates), cancel);
        }

        /// <summary>
        /// Collects matching ids for a prepared query via a doc-values scan (id_dv/ts_dv/line_dv),
        /// ordered by (timestamp asc, line asc). No stored-field retrieval, no result cap.
        /// </summary>
        private IList<long> SearchIds(Query query, System.Threading.CancellationToken cancel = default)
        {
            var searcher = GetSearcher();
            var collector = new IdSortCollector();
            SearchCancellable(searcher, query, collector, cancel);
            return collector.SortedIds();
        }

        /// <summary>
        /// Builds an executable query from a base query STRING plus a set of hidden templates, WITHOUT
        /// routing the (potentially hundreds of) templates through the classic QueryParser. Hidden
        /// templates are applied as a single <see cref="FieldCacheTermsFilter"/> MUST_NOT over the
        /// <c>tmpl</c> field instead of a giant <c>NOT (tmpl:"a" OR tmpl:"b" ...)</c> boolean of phrase
        /// queries - which is what made pattern "Hide all" pathologically slow (the parser + boolean
        /// rewrite of ~hundreds of quoted phrases containing special chars dominated the time, and it
        /// was paid on every pass: grid, summary and chart). Set-membership via the field cache is a
        /// single clause and runs in tens of ms. The base query still parses normally.
        /// </summary>
        private Query ComposeFilteredQuery(string baseQuery, IReadOnlyCollection<string> hiddenTemplates)
        {
            Query baseQ;
            if (string.IsNullOrWhiteSpace(baseQuery))
            {
                baseQ = new MatchAllDocsQuery();
            }
            else
            {
                baseQ = ParseQuery(NotFixRegex.Replace(baseQuery, "NOT"));
            }

            if (hiddenTemplates == null || hiddenTemplates.Count == 0)
            {
                return baseQ;
            }

            // tmpl is a LowerCaseKeywordAnalyzer field (whole value = one token), so the field-cache
            // keys are the lower-cased template strings. Match the indexing/query casing.
            var values = hiddenTemplates
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (values.Length == 0)
            {
                return baseQ;
            }

            var hiddenFilter = new FieldCacheTermsFilter("tmpl", values);
            return new BooleanQuery
            {
                { baseQ, Occur.MUST },
                { new ConstantScoreQuery(hiddenFilter), Occur.MUST_NOT },
            };
        }

        /// <summary>
        /// Validates a query string by attempting to parse it (does NOT execute). Returns whether it
        /// parsed and, if not, the parser's message. Used to vet detector/script-supplied insight
        /// queries so a bad one disables its "jump" rather than throwing when clicked. Cheap: parse only.
        /// </summary>
        public (bool ok, string error) TryValidateQuery(string queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString)) { return (true, null); }
            try
            {
                ParseQuery(NotFixRegex.Replace(queryString, "NOT"));
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
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
            /// <summary>
            /// Count of matched entries per source file (keyed by lower-cased full path). Only files
            /// present in the matched set appear. On a blank query this is every loaded file.
            /// </summary>
            public Dictionary<string, int> FileCounts { get; } = new Dictionary<string, int>();
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
        public FilteredSetSummary SummarizeFilteredSet(string queryString, System.Threading.CancellationToken cancel = default)
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
            return SummarizeQuery(query, cancel);
        }

        /// <summary>
        /// Summary variant applying hidden templates as a fast FieldCacheTermsFilter MUST_NOT
        /// (see <see cref="ComposeFilteredQuery"/>) instead of an OR'd phrase negation.
        /// </summary>
        public FilteredSetSummary SummarizeFilteredSet(string baseQuery, IReadOnlyCollection<string> hiddenTemplates,
            System.Threading.CancellationToken cancel = default)
        {
            return SummarizeQuery(ComposeFilteredQuery(baseQuery, hiddenTemplates), cancel);
        }

        private FilteredSetSummary SummarizeQuery(Query query, System.Threading.CancellationToken cancel = default)
        {
            var summary = new FilteredSetSummary();
            var searcher = GetSearcher();
            if (searcher.IndexReader.MaxDoc == 0)
            {
                return summary;
            }

            var collector = new FilteredSetSummaryCollector(summary);
            SearchCancellable(searcher, query, collector, cancel);
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

        /// <summary>
        /// Within the given query's matched set, returns the id of the newest entry whose event time is
        /// at or before <paramref name="ticks"/> (i.e. "round backwards" to the closest event), or null
        /// if none qualifies. A blank query covers the whole loaded set. Uses a single-hit descending
        /// sort, so it's cheap regardless of match count. Ties on timestamp break by line desc so the
        /// last line at that instant is chosen.
        /// </summary>
        public long? FindEntryIdAtOrBefore(string queryString, long ticks)
        {
            Query baseQ = null;
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                baseQ = ParseQuery(NotFixRegex.Replace(queryString, "NOT"));
            }
            return FindEntryIdAtOrBeforeCore(baseQ, ticks);
        }

        /// <summary>
        /// Timeline-click variant applying hidden PATTERN templates as a fast FieldCacheTermsFilter
        /// (see <see cref="ComposeFilteredQuery"/>) instead of an OR'd phrase negation - so a chart
        /// click while patterns are hidden doesn't re-parse a giant query (which froze the UI).
        /// </summary>
        public long? FindEntryIdAtOrBefore(string baseQuery, IReadOnlyCollection<string> hiddenTemplates, long ticks)
        {
            Query baseQ = (string.IsNullOrWhiteSpace(baseQuery) && (hiddenTemplates == null || hiddenTemplates.Count == 0))
                ? null
                : ComposeFilteredQuery(baseQuery, hiddenTemplates);
            return FindEntryIdAtOrBeforeCore(baseQ, ticks);
        }

        private long? FindEntryIdAtOrBeforeCore(Query baseQ, long ticks)
        {
            var searcher = GetSearcher();
            if (searcher.IndexReader.MaxDoc == 0) return null;

            // Constrain the matched set to events at or before the clicked time.
            var atOrBefore = NumericRangeQuery.NewInt64Range("timestamp", long.MinValue, ticks, true, true);

            Query combined;
            if (baseQ == null)
            {
                combined = atOrBefore;
            }
            else
            {
                combined = new BooleanQuery
                {
                    { baseQ, Occur.MUST },
                    { atOrBefore, Occur.MUST },
                };
            }

            var sort = new Sort(
                new SortField("timestamp", SortFieldType.INT64, true),
                new SortField("line", SortFieldType.INT64, true));
            var top = searcher.Search(combined, 1, sort);
            if (top.ScoreDocs.Length == 0) return null;

            var visitor = new DocumentStoredFieldVisitor("id");
            searcher.Doc(top.ScoreDocs[0].Doc, visitor);
            return long.Parse(visitor.Document.Get("id"));
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
            return BucketedLevelCountsForQuery(query, buckets);
        }

        /// <summary>
        /// Chart variant applying hidden templates as a fast FieldCacheTermsFilter MUST_NOT
        /// (see <see cref="ComposeFilteredQuery"/>) instead of an OR'd phrase negation.
        /// </summary>
        public TimeSeries BucketedLevelCounts(string baseQuery, IReadOnlyCollection<string> hiddenTemplates, int buckets,
            System.Threading.CancellationToken cancel = default)
        {
            return BucketedLevelCountsForQuery(ComposeFilteredQuery(baseQuery, hiddenTemplates), buckets, cancel);
        }

        private TimeSeries BucketedLevelCountsForQuery(Query query, int buckets, System.Threading.CancellationToken cancel = default)
        {
            if (buckets < 1) buckets = 1;

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
            SearchCancellable(searcher, query, collector, cancel);

            return new TimeSeries { Counts = counts, Buckets = buckets, MinTicks = min, MaxTicks = max };
        }

        /// <summary>
        /// One row of the Patterns view: a normalized message template, how many entries produced it,
        /// the time span over which it occurred, and the ids of the contributing entries (for drill-down).
        /// </summary>
        public sealed class LogPattern : System.ComponentModel.INotifyPropertyChanged
        {
            public string Template { get; set; }
            public int Count { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            /// <summary>Ids of the entries in this pattern bucket, for exact drill-down selection.</summary>
            public IReadOnlyList<long> Ids { get; set; }

            /// <summary>
            /// The raw regex-templates this pattern covers. When Drain folding is active a pattern may
            /// represent several near-duplicate regex-templates; drill-down and hide must query the
            /// <c>tmpl</c> field for ALL of them (see <see cref="PatternQuery"/>). Falls back to just
            /// <see cref="Template"/> when unfolded.
            /// </summary>
            public IReadOnlyList<string> MemberTemplates { get; set; }

            /// <summary>Per-level entry counts within this template (from lvl_dv).</summary>
            public int Info { get; set; }
            public int Warn { get; set; }
            public int Error { get; set; }
            public int Debug { get; set; }

            private bool _isShown = true;
            /// <summary>
            /// Whether this pattern's entries are shown (true) or hidden (false) in the current view.
            /// The Patterns list is a compute-on-demand snapshot, so toggling this updates the eye and
            /// the main grid (via the session hidden-templates set) but does NOT re-derive the list's
            /// counts - those stay as-of the last Compute (a stale hint surfaces the difference).
            /// </summary>
            public bool IsShown
            {
                get => _isShown;
                set
                {
                    if (_isShown == value) return;
                    _isShown = value;
                    OnPropertyChanged(nameof(IsShown));
                    OnPropertyChanged(nameof(EyeGlyph));
                    OnPropertyChanged(nameof(EyeToolTip));
                }
            }

            /// <summary>Eye glyph (Segoe MDL2 Assets): RedEye when shown, Hide when hidden.</summary>
            public string EyeGlyph => _isShown ? "\uE7B3" : "\uED1A";
            public string EyeToolTip => _isShown
                ? "Shown - click to hide this pattern's entries from the current view"
                : "Hidden - click to show this pattern's entries again";

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged(string n) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

            /// <summary>
            /// The level that most entries in this template carry ("INFO"/"WARN"/"ERROR"/"DEBUG"), or
            /// "" if the template has no counted entries. Ties break by severity (ERROR &gt; WARN &gt;
            /// INFO &gt; DEBUG) so a template with any errors reads as error-leaning. Handy for a compact
            /// column and color-coding in the Patterns grid.
            /// </summary>
            public string DominantLevel
            {
                get
                {
                    if (Error == 0 && Warn == 0 && Info == 0 && Debug == 0) return "";
                    // Highest count wins; severity breaks ties.
                    int max = Math.Max(Math.Max(Info, Warn), Math.Max(Error, Debug));
                    if (Error == max) return "ERROR";
                    if (Warn == max) return "WARN";
                    if (Info == max) return "INFO";
                    return "DEBUG";
                }
            }
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
            EnsureDrainMap();
            // Fold each raw regex-template (tmpl_dv) to its collapsed drain template while bucketing.
            // Identity when Drain is disabled/empty. Members let the view expand a pattern back to the
            // exact regex-templates it covers (for drill-down / hide, which query the tmpl field).
            var collector = new PatternCollector(_drainMap, _drainMembers);
            searcher.Search(query, collector);

            return collector.Build();
        }

        /// <summary>
        /// Lazily builds the Drain fold map (regexTemplate → drainTemplate) and its reverse
        /// (drainTemplate → member regexTemplates) from the DISTINCT tmpl_dv values in the current
        /// reader. Distinct values are read from the SortedDocValues term dictionary (one entry per
        /// unique template, not per doc), so this is cheap and — because BuildMap sorts its input —
        /// deterministic regardless of load order. No-op when already built or Drain is disabled.
        /// </summary>
        private void EnsureDrainMap()
        {
            if (!_drainEnabled) { _drainMap = null; _drainMembers = null; return; }
            if (_drainMap != null) return;

            var distinct = new List<string>();
            var sdv = MultiDocValues.GetSortedValues(_reader, "tmpl_dv");
            if (sdv != null)
            {
                int n = sdv.ValueCount;
                var scratch = new BytesRef();
                for (int ord = 0; ord < n; ord++)
                {
                    sdv.LookupOrd(ord, scratch);
                    string t = scratch.Utf8ToString();
                    if (!string.IsNullOrEmpty(t)) distinct.Add(t);
                }
            }

            var map = DrainTemplateMiner.BuildMap(distinct, _drainSimilarity);
            var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in map)
            {
                if (!members.TryGetValue(kv.Value, out var list))
                {
                    list = new List<string>();
                    members[kv.Value] = list;
                }
                list.Add(kv.Key);
            }

            _drainMap = map;
            _drainMembers = members;
        }

        /// <summary>
        /// One row of the Jobs view: a worker job and its observed lifespan. Start/End are the first
        /// and last event times seen across all of the job's worker/restart logs, so they reflect the
        /// real observed span even when a job ends without a clean "finished" line. Warn/Error are the
        /// per-level counts within the job; Ids are the contributing entries for drill-down.
        /// </summary>
        public sealed class JobInfo
        {
            public string JobId { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public TimeSpan Duration => LastSeen - FirstSeen;
            public int EntryCount { get; set; }
            public int WarnCount { get; set; }
            public int ErrorCount { get; set; }
            public IReadOnlyList<long> Ids { get; set; }

            /// <summary>Compact human-readable duration (e.g. "5d 16h", "3h 12m", "45s") for the grid.</summary>
            public string DurationText
            {
                get
                {
                    TimeSpan s = Duration;
                    if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
                    if (s.TotalHours >= 1) return $"{s.Hours}h {s.Minutes}m";
                    if (s.TotalMinutes >= 1) return $"{s.Minutes}m {s.Seconds}s";
                    return $"{s.Seconds}s";
                }
            }
        }

        /// <summary>
        /// Groups the matched set by worker job id (job_dv) and returns each job with its lifespan
        /// (first/last event), entry/warn/error counts, and contributing entry ids for drill-down.
        /// A blank query covers the whole loaded set. Single pass over matching docs via doc-values
        /// (job_dv/ts_dv/lvl_dv/id_dv), so it scales with the match count. Non-worker entries have no
        /// job_dv and are naturally excluded. Results are ordered by start time ascending.
        /// </summary>
        public IList<JobInfo> MineJobs(string queryString)
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
            var collector = new JobCollector();
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
    /// Collects the ids of all matching documents via doc-values (id_dv), tagged with their sort keys
    /// (ts_dv, line_dv), then returns them ordered by (timestamp asc, line asc) - the grid's global
    /// chronological order. This replaces retrieving the stored "id" field per hit + a capped
    /// TopFieldCollector: it reads only columnar doc-values (fast even for very large matched sets, e.g.
    /// a pattern "Hide all") and has no result cap, so the grid is never silently truncated.
    /// </summary>
    internal sealed class IdSortCollector : ICollector
    {
        private struct Row { public long Ts; public long Line; public long Id; }

        private readonly List<Row> _rows = new List<Row>();
        private NumericDocValues _id;
        private NumericDocValues _ts;
        private NumericDocValues _line;

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _id = context.AtomicReader.GetNumericDocValues("id_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            _line = context.AtomicReader.GetNumericDocValues("line_dv");
        }

        public void Collect(int doc)
        {
            if (_id == null) return;
            _rows.Add(new Row
            {
                Ts = _ts?.Get(doc) ?? 0,
                Line = _line?.Get(doc) ?? 0,
                Id = _id.Get(doc),
            });
        }

        public bool AcceptsDocsOutOfOrder => true;

        /// <summary>Matching ids ordered by (timestamp asc, line asc), matching the grid's global order.</summary>
        public IList<long> SortedIds()
        {
            _rows.Sort((a, b) =>
            {
                int c = a.Ts.CompareTo(b.Ts);
                if (c != 0) return c;
                return a.Line.CompareTo(b.Line);
            });
            var ids = new long[_rows.Count];
            for (int i = 0; i < _rows.Count; i++) ids[i] = _rows[i].Id;
            return ids;
        }
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
            public int Info;
            public int Warn;
            public int Error;
            public int Debug;
        }

        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>(4096);
        private readonly BytesRef _scratch = new BytesRef();
        private SortedDocValues _tmpl;
        private NumericDocValues _ts;
        private NumericDocValues _id;
        private NumericDocValues _lvl;

        // Optional second-stage fold: maps a raw regex-template (tmpl_dv) to a collapsed "drain"
        // template so near-duplicate templates bucket together. Null => group by raw template. Members
        // maps a drain template back to its regex-templates so the view can expand a pattern for
        // drill-down / hide (which query the tmpl field by exact regex-template).
        private readonly IReadOnlyDictionary<string, string> _fold;
        private readonly IReadOnlyDictionary<string, List<string>> _members;

        public PatternCollector(
            IReadOnlyDictionary<string, string> fold = null,
            IReadOnlyDictionary<string, List<string>> members = null)
        {
            _fold = fold;
            _members = members;
        }

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            _tmpl = context.AtomicReader.GetSortedDocValues("tmpl_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            _id = context.AtomicReader.GetNumericDocValues("id_dv");
            _lvl = context.AtomicReader.GetNumericDocValues("lvl_dv");
        }

        public void Collect(int doc)
        {
            if (_tmpl == null || _ts == null || _id == null) return;

            _tmpl.Get(doc, _scratch);
            string template = _scratch.Utf8ToString();
            // Fold to the collapsed drain template when a map is present (identity otherwise).
            if (_fold != null && _fold.TryGetValue(template, out var folded)) template = folded;
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

            // Per-level tally from lvl_dv (0=INFO,1=WARN,2=ERROR,3=DEBUG,4=other). Near-zero extra cost
            // since we already visit every matching doc here.
            switch ((int)(_lvl?.Get(doc) ?? 4))
            {
                case 0: bucket.Info++; break;
                case 1: bucket.Warn++; break;
                case 2: bucket.Error++; break;
                case 3: bucket.Debug++; break;
            }
        }

        public bool AcceptsDocsOutOfOrder => true;

        /// <summary>Materializes the accumulated buckets into result rows, ordered by count descending.</summary>
        public IList<LogSearchIndex.LogPattern> Build()
        {
            var result = new List<LogSearchIndex.LogPattern>(_buckets.Count);
            foreach (var kv in _buckets)
            {
                // Members are the raw regex-templates this (possibly folded) pattern covers, used to
                // expand drill-down / hide back to exact tmpl queries. When unfolded, the member set is
                // just the template itself.
                IReadOnlyList<string> members;
                if (_members != null && _members.TryGetValue(kv.Key, out var m)) members = m;
                else members = new[] { kv.Key };

                result.Add(new LogSearchIndex.LogPattern
                {
                    Template = kv.Key,
                    Count = kv.Value.Ids.Count,
                    FirstSeen = new DateTime(kv.Value.FirstTicks),
                    LastSeen = new DateTime(kv.Value.LastTicks),
                    Ids = kv.Value.Ids,
                    Info = kv.Value.Info,
                    Warn = kv.Value.Warn,
                    Error = kv.Value.Error,
                    Debug = kv.Value.Debug,
                    MemberTemplates = members,
                });
            }
            result.Sort((a, b) => b.Count.CompareTo(a.Count));
            return result;
        }
    }

    /// <summary>
    /// Collector that groups matching documents by worker job id (job_dv), accumulating per-job
    /// first/last-seen (ts_dv), entry/warn/error counts (lvl_dv) and the contributing ids (id_dv).
    /// Documents with no job_dv (non-worker logs) are skipped. Reads only doc-values, so the whole
    /// pass is a fast columnar scan - the job analog of PatternCollector.
    /// </summary>
    internal sealed class JobCollector : ICollector
    {
        private sealed class Bucket
        {
            public List<long> Ids = new List<long>();
            public long FirstTicks;
            public long LastTicks;
            public int Warn;
            public int Error;
        }

        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>(256);
        private readonly BytesRef _scratch = new BytesRef();
        private SortedDocValues _job;
        private NumericDocValues _ts;
        private NumericDocValues _lvl;
        private NumericDocValues _id;

        public void SetScorer(Scorer scorer) { }

        public void SetNextReader(AtomicReaderContext context)
        {
            // job_dv may be absent for a segment where no entry was a worker log.
            _job = context.AtomicReader.GetSortedDocValues("job_dv");
            _ts = context.AtomicReader.GetNumericDocValues("ts_dv");
            _lvl = context.AtomicReader.GetNumericDocValues("lvl_dv");
            _id = context.AtomicReader.GetNumericDocValues("id_dv");
        }

        public void Collect(int doc)
        {
            if (_job == null || _ts == null || _id == null) return;

            // Ord < 0 means this document has no job_dv value (not a worker log) - skip it.
            int ord = _job.GetOrd(doc);
            if (ord < 0) return;

            _job.LookupOrd(ord, _scratch);
            string jobId = _scratch.Utf8ToString();
            long ticks = _ts.Get(doc);
            long id = _id.Get(doc);

            if (!_buckets.TryGetValue(jobId, out var bucket))
            {
                bucket = new Bucket { FirstTicks = ticks, LastTicks = ticks };
                _buckets[jobId] = bucket;
            }
            bucket.Ids.Add(id);
            if (ticks < bucket.FirstTicks) bucket.FirstTicks = ticks;
            if (ticks > bucket.LastTicks) bucket.LastTicks = ticks;

            switch ((int)(_lvl?.Get(doc) ?? 4))
            {
                case 1: bucket.Warn++; break;
                case 2: bucket.Error++; break;
            }
        }

        public bool AcceptsDocsOutOfOrder => true;

        /// <summary>Materializes the accumulated buckets into result rows, ordered by start time ascending.</summary>
        public IList<LogSearchIndex.JobInfo> Build()
        {
            var result = new List<LogSearchIndex.JobInfo>(_buckets.Count);
            foreach (var kv in _buckets)
            {
                result.Add(new LogSearchIndex.JobInfo
                {
                    JobId = kv.Key,
                    FirstSeen = new DateTime(kv.Value.FirstTicks),
                    LastSeen = new DateTime(kv.Value.LastTicks),
                    EntryCount = kv.Value.Ids.Count,
                    WarnCount = kv.Value.Warn,
                    ErrorCount = kv.Value.Error,
                    Ids = kv.Value.Ids,
                });
            }
            result.Sort((a, b) => a.FirstSeen.CompareTo(b.FirstSeen));
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
        private SortedDocValues _file;

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
            // file_dv is single-valued; absent only if a segment had no entry with a file path.
            _file = context.AtomicReader.GetSortedDocValues("file_dv");
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

            if (_file != null)
            {
                int ord = _file.GetOrd(doc);
                if (ord >= 0)
                {
                    _file.LookupOrd(ord, _scratch);
                    string file = _scratch.Utf8ToString();
                    _summary.FileCounts.TryGetValue(file, out int c);
                    _summary.FileCounts[file] = c + 1;
                }
            }
        }

        public bool AcceptsDocsOutOfOrder => true;
    }
}
