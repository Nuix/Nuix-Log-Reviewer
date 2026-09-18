using NuixLogReviewer.LogRepository.Classifiers;
using NuixLogReviewerObjects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class NuixLogRepo
    {
        private Guid repoGuid = Guid.NewGuid();

        public string DatabaseFile { get; private set; }
        public LogDatabase Database { get; private set; }
        public string LuceneDirectory { get; private set; }
        public LogSearchIndex SearchIndex { get; private set; }

        public static string RepoRootDirectory { get; set; }

        public bool RepoDisposed { get; private set; }

        public string RepoDirectory
        {
            get; private set;
        }

        public NuixLogRepo()
        {
            RepoDirectory = Path.Combine(RepoRootDirectory, repoGuid.ToString());
            DatabaseFile = Path.Combine(RepoDirectory, "records.db");
            LuceneDirectory = Path.Combine(RepoDirectory, "Lucene");

            Directory.CreateDirectory(RepoDirectory);
            Directory.CreateDirectory(LuceneDirectory);

            Database = new LogDatabase(DatabaseFile);
            SearchIndex = new LogSearchIndex(LuceneDirectory);

            RepoDisposed = false;
        }

        public void LoadLogFiles(IEnumerable<string> logFiles, ProgressBroadcaster pb = null)
        {
            if (pb == null)
            {
                // Rather than check for null all over the place we will just
                // user a dummy broadcaster that has no listeners.
                pb = new ProgressBroadcaster();
            }

            // Drop the LogEntry indexes so bulk inserts are fast; we rebuild them after load.
            // (Lookup-table indexes are created at DB init and left in place - they speed up
            // the Value lookups that happen during load.)
            pb.BroadcastStatus("Dropping database indexes...");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_EntryID;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_TimeStamp;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_LineNumber;");

            // Skip empty files up front, preserving the caller's order. That order defines the id
            // assignment order below (file-order, then in-line order), identical to the previous
            // one-file-at-a-time loader.
            string[] files = logFiles
                .Where(f => { var fi = new FileInfo(f); return fi.Exists && fi.Length > 0; })
                .ToArray();

            SearchIndex.BeginWrite();
            try
            {
                LoadFilesPipeline(files, pb);
            }
            finally
            {
                SearchIndex.EndWrite();
            }

            // SQLite is faster building whole index at once rather than on each insert, so earlier
            // we dropped the LogEntry indexes and now we rebuild them.
            pb.BroadcastStatus("Rebuilding database indexes...");
            Database.ExecuteNonQuery("CREATE INDEX IDX_EntryID ON LogEntry (ID);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_TimeStamp ON LogEntry (TimeStamp);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_LineNumber ON LogEntry (LineNumber);");
        }

        /// <summary>
        /// Single load-wide producer/consumer pipeline shared across ALL files:
        /// <list type="bullet">
        /// <item>A bounded pool of reader+classifier workers reads and classifies files concurrently,
        /// each writing its entries (in line order) into that file's own ordered queue.</item>
        /// <item>An ordering coordinator drains those per-file queues strictly in file order into a
        /// single shared insert queue, so the id assignment order is identical to the old sequential
        /// loader (file 0's entries, then file 1's, ...).</item>
        /// <item>One shared DB consumer assigns ids and inserts (single SQLite writer), then hands
        /// entries to a shared Lucene indexer pool.</item>
        /// </list>
        /// This overlaps the CPU-heavy read/classify/index of later files with the DB insertion of
        /// earlier ones, while preserving every ordering and single-writer invariant of the original.
        /// </summary>
        private void LoadFilesPipeline(string[] files, ProgressBroadcaster pb)
        {
            if (files.Length == 0) { return; }

            int indexingConcurrency = 8;
            // Read+classify is CPU-bound (many classifiers per entry); cap workers to cores but never
            // more than the number of files, and at least 1.
            int readConcurrency = Math.Max(1, Math.Min(files.Length, Environment.ProcessorCount));

            // Ids are assigned on the single DB thread; seed once from the current high-water mark.
            long nextId = Database.GetHighestLogEntryID();

            SQLiteBatchInserter batchInserter = Database.CreateBatchInserter(5000);
            batchInserter.Begin(Database.GetEmbeddedSQL("NuixLogReviewer.LogRepository.InsertLogEntry.sqlite"));

            long recordCount = 0;

            // One ordered queue per file. Bounded so a very large file can't balloon memory while the
            // coordinator is still draining an earlier file. Each queue ends with a null sentinel.
            var perFileQueues = new BlockingCollection<NuixLogEntry>[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                perFileQueues[i] = new BlockingCollection<NuixLogEntry>(boundedCapacity: 20000);
            }

            var toInsert = new BlockingCollection<NuixLogEntry>(boundedCapacity: 50000);
            var toIndex = new BlockingCollection<NuixLogEntry>(boundedCapacity: 50000);

            // ==== Reader+classifier worker pool ====
            // Workers claim the next unread file index atomically and process it end to end into that
            // file's queue. Each worker owns its own classifier instances (the classifiers are
            // stateless per-entry, but per-worker instances keep us safe from any future state).
            int nextFileIndex = -1;
            Task[] readers = new Task[readConcurrency];
            for (int w = 0; w < readConcurrency; w++)
            {
                readers[w] = Task.Factory.StartNew(() =>
                {
                    List<IEntryClassifier> classifiers = getAllClassifiers();
                    while (true)
                    {
                        int fileIndex = System.Threading.Interlocked.Increment(ref nextFileIndex);
                        if (fileIndex >= files.Length) { break; }

                        var queue = perFileQueues[fileIndex];
                        try
                        {
                            var reader = new NuixLogReader(files[fileIndex]);
                            foreach (var entry in reader)
                            {
                                HashSet<string> flags = new HashSet<string>();
                                foreach (var classifier in classifiers)
                                {
                                    var calculatedFlags = classifier.Classify(entry);
                                    if (calculatedFlags != null)
                                    {
                                        foreach (var calculatedFlag in calculatedFlags)
                                        {
                                            flags.Add(calculatedFlag.ToLower());
                                        }
                                    }
                                }
                                entry.Flags = flags;
                                queue.Add(entry);
                            }
                        }
                        catch (Exception ex)
                        {
                            // "Load what's present": one unreadable/locked/vanished file must not abort
                            // the load or abandon the other files assigned to this worker. Skip it (its
                            // queue is still completed in finally) and carry on with the next file.
                            System.Diagnostics.Debug.WriteLine(
                                $"Skipping unreadable log file '{files[fileIndex]}': {ex.Message}");
                        }
                        finally
                        {
                            // Always close the queue so the coordinator never blocks forever on a file
                            // that failed to read.
                            queue.CompleteAdding();
                        }
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // ==== Ordering coordinator: drain per-file queues in file order into the shared insert queue ====
            Task coordinator = Task.Factory.StartNew(() =>
            {
                for (int i = 0; i < files.Length; i++)
                {
                    pb.BroadcastStatus("Loading from " + files[i]);
                    foreach (var entry in perFileQueues[i].GetConsumingEnumerable())
                    {
                        toInsert.Add(entry);
                    }
                    perFileQueues[i].Dispose();
                }
                toInsert.CompleteAdding();
            }, TaskCreationOptions.LongRunning);

            // ==== Single DB consumer: assign ids + insert (one SQLite writer), then fan out to index ====
            Task dbConsumer = Task.Factory.StartNew(() =>
            {
                DateTime lastProgress = DateTime.Now;

                foreach (var entry in toInsert.GetConsumingEnumerable())
                {
                    nextId++;

                    entry.ID = nextId;
                    batchInserter["@id"] = entry.ID;
                    batchInserter["@linenumber"] = entry.LineNumber;
                    batchInserter["@filename"] = Database.GetFilenameID(entry.FilePath);
                    batchInserter["@timestamp"] = entry.TimeStamp.ToFileTime();
                    batchInserter["@channel"] = Database.GetChannelID(entry.Channel);
                    batchInserter["@elapsed"] = entry.Elapsed.TotalMilliseconds;
                    batchInserter["@level"] = Database.GetLevelID(entry.Level);
                    batchInserter["@source"] = Database.GetSourceID(entry.Source);
                    batchInserter["@content"] = entry.Content;
                    batchInserter["@flags"] = String.Join(" ", entry.Flags);
                    batchInserter.Insert();

                    recordCount++;

                    if ((DateTime.Now - lastProgress).TotalMilliseconds >= 500)
                    {
                        pb.BroadcastProgress(recordCount);
                        lastProgress = DateTime.Now;
                    }

                    toIndex.Add(entry);
                }

                toIndex.CompleteAdding();
            }, TaskCreationOptions.LongRunning);

            // ==== Shared Lucene indexer pool ====
            // All Lucene Document construction lives in LogSearchIndex.IndexLogEntry(entry) so the
            // write-time field configuration stays in one place and can't drift from the query-time
            // analyzer configuration. IndexWriter.AddDocument is thread-safe, so we fan this out.
            Task[] indexers = new Task[indexingConcurrency];
            for (int i = 0; i < indexingConcurrency; i++)
            {
                indexers[i] = Task.Factory.StartNew(() =>
                {
                    foreach (var entry in toIndex.GetConsumingEnumerable())
                    {
                        SearchIndex.IndexLogEntry(entry);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Wait for the whole pipeline to drain, in dependency order.
            Task.WaitAll(readers);
            Task.WaitAll(coordinator, dbConsumer);
            pb.BroadcastStatus("Waiting for indexing to complete...");
            Task.WaitAll(indexers);

            pb.BroadcastProgress(recordCount);

            // Flush any pending inserts and release the (single) batch inserter.
            batchInserter.Complete();
            Database.ReleaseBatchInserter(batchInserter);

            toInsert.Dispose();
            toIndex.Dispose();
        }

        /// <summary>
        /// Oldest event time across the loaded set, or null if nothing is loaded. Derived from the
        /// search index timestamp bounds computed at load time.
        /// </summary>
        public DateTime? LoadedMinTime =>
            SearchIndex.MinTimestampTicks.HasValue ? new DateTime(SearchIndex.MinTimestampTicks.Value) : (DateTime?)null;

        /// <summary>
        /// Newest event time across the loaded set, or null if nothing is loaded.
        /// </summary>
        public DateTime? LoadedMaxTime =>
            SearchIndex.MaxTimestampTicks.HasValue ? new DateTime(SearchIndex.MaxTimestampTicks.Value) : (DateTime?)null;

        /// <summary>
        /// Time-bucketed INFO/WARN/ERROR counts for the given query's matched set, for the chart.
        /// </summary>
        public LogSearchIndex.TimeSeries GetTimeSeries(string query, int buckets)
        {
            return SearchIndex.BucketedLevelCounts(query, buckets);
        }

        /// <summary>
        /// Mines the given query's matched set into normalized message-template groups (the Patterns
        /// view), each with a count, first/last-seen and the contributing entry ids for drill-down.
        /// </summary>
        public IList<LogSearchIndex.LogPattern> GetPatterns(string query)
        {
            return SearchIndex.MinePatterns(query);
        }

        /// <summary>
        /// Groups the given query's matched set into worker jobs (the Jobs view), each with its
        /// observed lifespan (first/last event), entry/warn/error counts and the contributing entry
        /// ids for drill-down. Consistent with the other views, this reflects the current filtered set.
        /// </summary>
        public IList<LogSearchIndex.JobInfo> GetJobs(string query)
        {
            return SearchIndex.MineJobs(query);
        }

        /// <summary>
        /// Analyzes the WHOLE loaded corpus ("of the logs loaded, here are threads to pull on"): builds
        /// a frozen <see cref="Insights.InsightContext"/> from the existing whole-set summary passes and
        /// runs the built-in insight detectors over it. Whole-set and blank-query (unaffected by the
        /// view's hidden classifiers/files/patterns), computed on demand off the UI thread. Detector
        /// queries are not validated here (built-ins are trusted); the caller can pass TryValidateQuery
        /// for untrusted sources later. Returns findings grouped by source, most-severe first.
        /// </summary>
        public IList<Insights.Insight> BuildInsights(int timelineBuckets = 200)
        {
            var summary = SearchIndex.SummarizeFilteredSet("");            // whole set
            var jobs = SearchIndex.MineJobs("");
            var patterns = SearchIndex.MinePatterns("");
            var timeline = SearchIndex.BucketedLevelCounts("", timelineBuckets);
            var fileDisplay = Database.FileDisplayNames();

            var ctx = new Insights.InsightContext(summary, fileDisplay, jobs, patterns, timeline);

            var thresholds = new Insights.InsightThresholds();
            var detectors = new List<Insights.IInsightDetector>
            {
                new Insights.ErrorSpikeDetector(thresholds),
                new Insights.ErrorDominantFileDetector(thresholds),
                new Insights.JobFailureDetector(thresholds),
                new Insights.PatternAnomalyDetector(thresholds),
                new Insights.SilenceGapDetector(thresholds),
                new Insights.ErrorRampUpDetector(thresholds),
                new Insights.ErrorOnsetDetector(thresholds),
            };
            // Append user-supplied scripted detectors (Configuration/InsightScripts/*.js). Reloaded each
            // Analyze so edits are picked up without a restart; failures are logged and skipped.
            detectors.AddRange(Insights.InsightScriptLoader.LoadDetectors());

            var engine = new Insights.InsightEngine(detectors);

            // Built-in detectors produce trusted, compiler-built queries, but validate anyway so a bad
            // one degrades gracefully (jump disabled) rather than erroring on click - cheap (parse only).
            return engine.Run(ctx, q => SearchIndex.TryValidateQuery(q));
        }

        public long? FindEntryIdAtOrBefore(string query, long ticks)
        {
            return SearchIndex.FindEntryIdAtOrBefore(query, ticks);
        }

        /// <summary>
        /// Timeline click-to-scroll with hidden PATTERN templates applied via the fast set-membership
        /// filter path (not an OR'd phrase negation), so a chart click while patterns are hidden stays
        /// snappy. <paramref name="query"/> carries the (few) hidden classifier/file clauses.
        /// </summary>
        public long? FindEntryIdAtOrBefore(string query, IReadOnlyCollection<string> hiddenTemplates, long ticks)
        {
            return SearchIndex.FindEntryIdAtOrBefore(query, hiddenTemplates, ticks);
        }

        /// <summary>
        /// Single-pass summary (total, per-level and per-flag counts, time span) for a query's matched
        /// set. Used to compute classifier counts on the unfiltered-by-visibility set and the
        /// "N rows excluded by classifiers" figure, independent of the effective (hidden-filtered) query.
        /// </summary>
        public LogSearchIndex.FilteredSetSummary Summarize(string query, System.Threading.CancellationToken cancel = default)
        {
            return SearchIndex.SummarizeFilteredSet(query, cancel);
        }

        /// <summary>
        /// Builds a grid response containing exactly the entries with the given ids (used by the
        /// Patterns view drill-down). Level counts and the time span are summarized from the index so
        /// the status readouts and chart stay consistent with the shown rows.
        /// </summary>
        public LogEntrySearchResponse BuildResponseForIds(IList<long> ids)
        {
            var response = new LogEntrySearchResponse(new NuixLogEntryItemProvider()
            {
                Ids = ids,
                SourceRepository = this
            }, 1000);

            var summary = SearchIndex.SummarizeIds(ids);
            response.InfoEntryCount = summary.Info;
            response.WarnEntryCount = summary.Warn;
            response.ErrorEntryCount = summary.Error;
            response.DebugEntryCount = summary.Debug;
            response.FilteredMinTime = summary.MinTicks.HasValue ? new DateTime(summary.MinTicks.Value) : (DateTime?)null;
            response.FilteredMaxTime = summary.MaxTicks.HasValue ? new DateTime(summary.MaxTicks.Value) : (DateTime?)null;
            return response;
        }

        /// <summary>Time-series (bucketed level counts) for an explicit id set, for the Patterns drill-down chart.</summary>
        public LogSearchIndex.TimeSeries GetTimeSeriesForIds(IList<long> ids, int buckets)
        {
            return SearchIndex.BucketedLevelCountsForIds(ids, buckets);
        }

        public LogEntrySearchResponse Search(string query)
        {
            return Search(query, null);
        }

        /// <summary>
        /// Search + summarize where hidden PATTERN templates are applied as a fast set-membership filter
        /// (FieldCacheTermsFilter MUST_NOT) rather than an OR'd phrase negation baked into the query
        /// string. <paramref name="query"/> is the base query already carrying the (few) hidden
        /// classifier/file clauses; <paramref name="hiddenTemplates"/> are the raw regex-templates to
        /// exclude. This is what keeps pattern "Hide all" fast on large template sets.
        /// </summary>
        public LogEntrySearchResponse SearchWithHiddenTemplates(
            string query, IReadOnlyCollection<string> hiddenTemplates,
            LogSearchIndex.FilteredSetSummary reuseSummary,
            System.Threading.CancellationToken cancel = default)
        {
            IList<long> ids = SearchIndex.Search(this, query, hiddenTemplates, cancel);
            LogEntrySearchResponse result = new LogEntrySearchResponse(new NuixLogEntryItemProvider()
            {
                Ids = ids,
                SourceRepository = this
            }, 1000);

            var summary = reuseSummary ?? SearchIndex.SummarizeFilteredSet(query, hiddenTemplates, cancel);
            result.InfoEntryCount = summary.Info;
            result.WarnEntryCount = summary.Warn;
            result.ErrorEntryCount = summary.Error;
            result.DebugEntryCount = summary.Debug;
            result.FilteredMinTime = summary.MinTicks.HasValue ? new DateTime(summary.MinTicks.Value) : (DateTime?)null;
            result.FilteredMaxTime = summary.MaxTicks.HasValue ? new DateTime(summary.MaxTicks.Value) : (DateTime?)null;
            foreach (var kv in summary.FlagCounts) { result.FlagCounts[kv.Key] = kv.Value; }
            return result;
        }

        /// <summary>Time-series for a base query with hidden PATTERN templates applied via the filter path.</summary>
        public LogSearchIndex.TimeSeries GetTimeSeriesWithHiddenTemplates(
            string query, IReadOnlyCollection<string> hiddenTemplates, int buckets,
            System.Threading.CancellationToken cancel = default)
        {
            return SearchIndex.BucketedLevelCounts(query, hiddenTemplates, buckets, cancel);
        }

        /// <summary>
        /// Runs a search. When <paramref name="reuseSummary"/> is provided (the caller already computed
        /// <see cref="LogSearchIndex.SummarizeFilteredSet"/> for the SAME query - e.g. the common case
        /// where nothing is hidden, so the effective query equals the base query the classifier/file
        /// tables were summarized from), it's reused instead of running the full doc-values summary pass
        /// again. This removes one whole-index scan from the hot path (notably plain "clear search").
        /// </summary>
        public LogEntrySearchResponse Search(string query, LogSearchIndex.FilteredSetSummary reuseSummary)
        {
            IList<long> ids = SearchIndex.Search(this, query);
            LogEntrySearchResponse result = new LogEntrySearchResponse(new NuixLogEntryItemProvider()
            {
                Ids = ids, //Database.SortIds(ids),
                SourceRepository = this
            }, 1000);

            // Everything the status bar / Classifiers table needs about the matched set - per-level
            // counts, per-flag counts and the event-time span - is computed in ONE doc-values pass.
            // This replaces the old approach of running a separate Lucene count per level plus one
            // per classifier flag (and a DISTINCT(Flags) DB scan), which made broad queries such as
            // the blank "clear search" slow (it scanned the whole index ~25-30 times).
            var summary = reuseSummary ?? SearchIndex.SummarizeFilteredSet(query);

            result.InfoEntryCount = summary.Info;
            result.WarnEntryCount = summary.Warn;
            result.ErrorEntryCount = summary.Error;
            result.DebugEntryCount = summary.Debug;

            result.FilteredMinTime = summary.MinTicks.HasValue ? new DateTime(summary.MinTicks.Value) : (DateTime?)null;
            result.FilteredMaxTime = summary.MaxTicks.HasValue ? new DateTime(summary.MaxTicks.Value) : (DateTime?)null;

            // Per-classifier counts for the current filtered set. Only flags actually present in the
            // matched set are reported (the collector only sees flags it encounters), which is exactly
            // what the UI wants - it hides zero-count flags anyway.
            foreach (var kv in summary.FlagCounts)
            {
                result.FlagCounts[kv.Key] = kv.Value;
            }

            return result;
        }

        public void DisposeRepo()
        {
            if (!RepoDisposed)
            {
                try
                {
                    // Dispose resources first to release file locks
                    SearchIndex?.Dispose();
                    // Database doesn't implement IDisposable, but connections are auto-closed
                    
                    // Small delay to ensure file handles are released
                    System.Threading.Thread.Sleep(100);
                    
                    // Now delete the directory
                    if (Directory.Exists(RepoDirectory))
                    {
                        Directory.Delete(RepoDirectory, true);
                    }
                }
                catch (Exception)
                {
                    // If deletion fails, try again after a longer delay
                    System.Threading.Thread.Sleep(500);
                    try
                    {
                        if (Directory.Exists(RepoDirectory))
                        {
                            Directory.Delete(RepoDirectory, true);
                        }
                    }
                    catch
                    {
                        // If it still fails, ignore - the temp directory will be cleaned up eventually
                    }
                }
                finally
                {
                    RepoDisposed = true;
                }
            }
        }

        // Use reflection to get an instance of all defined compiled classifiers (classes that implement
        // IEntryClassifier and have a parameterless constructor), then append the scripted classifiers
        // loaded from the ClassifierScripts directory. Called once per load worker, so each worker gets
        // its own instances - including a private Jint engine per scripted classifier (engines are
        // single-threaded).
        private List<IEntryClassifier> getAllClassifiers()
        {
            var iEntryClassifierType = typeof(IEntryClassifier);
            var classifierTypes = Assembly.GetExecutingAssembly().GetExportedTypes()
                .Where(t => !t.IsInterface && !t.IsAbstract && iEntryClassifierType.IsAssignableFrom(t))
                // The scripted classifier is constructed from a prepared script definition, not by
                // reflection; skip it (and anything else lacking a public parameterless constructor).
                .Where(t => t.GetConstructor(Type.EmptyTypes) != null);

            List<IEntryClassifier> result = new List<IEntryClassifier>();
            foreach (var classifierType in classifierTypes)
            {
                result.Add((IEntryClassifier)Activator.CreateInstance(classifierType));
            }

            // Scripted classifiers (sandboxed JS from ClassifierScripts/*.js), one fresh engine each.
            result.AddRange(Classifiers.Scripting.ScriptClassifierLoader.CreateClassifiersForWorker());

            return result;
        }
    }
}
