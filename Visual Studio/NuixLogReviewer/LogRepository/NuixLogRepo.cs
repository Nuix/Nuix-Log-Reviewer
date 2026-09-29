using NuixLogReviewer.LogRepository.Classifiers;
using NuixLogReviewerObjects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
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

        /// <summary>
        /// Best-effort sweep of orphaned temp-repo folders left by previous sessions that didn't shut
        /// down cleanly (crash / kill / power loss), since <see cref="DisposeRepo"/> only runs on a normal
        /// exit or a new load. Called at startup BEFORE the process creates its own repo, so every
        /// existing subfolder under <see cref="RepoRootDirectory"/> is by definition from a prior run.
        ///
        /// To stay safe if another instance happens to be running concurrently, folders modified within
        /// <paramref name="minAgeMinutes"/> minutes are skipped (a live session writes to its folder).
        /// Returns the number of folders removed. Never throws.
        /// </summary>
        public static int CleanupOrphanedRepos(int minAgeMinutes = 10)
        {
            int removed = 0;
            try
            {
                string root = RepoRootDirectory;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { return 0; }

                DateTime cutoff = DateTime.UtcNow.AddMinutes(-Math.Abs(minAgeMinutes));
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        // Skip folders touched recently - a concurrently-running instance's live repo.
                        DateTime touched = Directory.GetLastWriteTimeUtc(dir);
                        if (touched > cutoff) { continue; }

                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch
                    {
                        // A folder may be locked by a live instance or mid-delete; skip it. The next
                        // startup will retry. Best-effort by design.
                    }
                }
            }
            catch
            {
                // Enumerating the root failed (missing/unwritable); nothing to clean.
            }
            return removed;
        }

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

            // The loaded set changed; drop cached derived views (all-ids list, file display names) so
            // they rebuild against the new data on next access.
            Database.InvalidateDerivedCaches();
        }

        /// <summary>
        /// Single load-wide producer/consumer pipeline shared across ALL files:
        /// <list type="bullet">
        /// <item>A bounded pool of reader+classifier workers reads and classifies files concurrently,
        /// streaming their entries straight into ONE shared bounded insert queue.</item>
        /// <item>One shared DB consumer assigns ids and inserts (single SQLite writer), then hands
        /// entries to a shared Lucene indexer pool.</item>
        /// </list>
        /// Ids are assigned in arrival order (NOT file/load order): nothing reads entries by id - every
        /// view sorts by (TimeStamp ASC, LineNumber ASC) and id is only a unique key - so no ordering
        /// coordinator is needed. This bounds total in-flight memory to the shared queue's capacity
        /// (independent of file count/core count) and removes the head-of-line stall a slow early file
        /// used to cause. The single-SQLite-writer invariant is preserved.
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

            // A SINGLE shared bounded queue feeds the DB consumer. Reader workers add classified entries
            // straight into it (no per-file queues, no strict-order coordinator): id-assignment order no
            // longer needs to match file/load order because NOTHING reads entries by id - the grid and
            // every drill sort by (TimeStamp ASC, LineNumber ASC), and id is only a unique key. Dropping
            // the per-file-queue design removes the head-of-line stall (a slow early file used to pin
            // every later file's buffered entries) and bounds total in-flight memory to this one queue's
            // capacity regardless of file count or core count. Intra-file line order is preserved (a file
            // is read sequentially); only cross-file ties on an identical timestamp become arbitrary,
            // which is invisible in the (TimeStamp, LineNumber)-sorted views.
            var toInsert = new BlockingCollection<NuixLogEntry>(boundedCapacity: 16000);
            var toIndex = new BlockingCollection<NuixLogEntry>(boundedCapacity: 16000);

            // ==== Reader+classifier worker pool ====
            // Workers claim the next unread file index atomically and stream its classified entries into
            // the shared insert queue. Each worker owns its own classifier instances (stateless per
            // entry, but per-worker instances keep us safe from any future state).
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

                        pb.BroadcastStatus("Loading from " + files[fileIndex]);
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
                                toInsert.Add(entry);
                            }
                        }
                        catch (Exception ex)
                        {
                            // "Load what's present": one unreadable/locked/vanished file must not abort
                            // the load or abandon the other files. Skip it and carry on.
                            System.Diagnostics.Debug.WriteLine(
                                $"Skipping unreadable log file '{files[fileIndex]}': {ex.Message}");
                        }
                    }
                }, TaskCreationOptions.LongRunning);
            }

            // Complete the shared insert queue exactly ONCE, after every reader has finished. Runs on its
            // own task so the DB consumer below can drain concurrently while the readers are still going.
            Task readerCompletion = Task.Factory.StartNew(() =>
            {
                Task.WaitAll(readers);
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

            // Wait for the whole pipeline to drain, in dependency order. readerCompletion waits on all
            // readers then closes the shared queue, so waiting on it + dbConsumer covers read+insert.
            Task.WaitAll(readerCompletion, dbConsumer);
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
        /// Reconstructs Automate workflow executions (with their operation breakdown) from the engine
        /// logs in the current filtered set. Unlike jobs (path-derived), workflows are delineated by
        /// lifecycle lines the engine logs, so this focuses on the two relevant source components,
        /// reads their entries, and mines them (see <see cref="WorkflowExecutionMiner"/>).
        /// </summary>
        public IList<WorkflowExecutionMiner.WorkflowInfo> GetWorkflows(string query)
        {
            var ids = SearchIndex.WorkflowCandidateIds(this, query);
            if (ids == null || ids.Count == 0) return new List<WorkflowExecutionMiner.WorkflowInfo>();
            var entries = Database.ReadEntries(ids);
            return WorkflowExecutionMiner.Mine(entries);
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

            // Workflow executions across the whole set (blank query), mined the same way GetWorkflows does
            // (candidate ids -> read entries -> mine). Empty for corpora with no workflow-execution logs.
            var workflowIds = SearchIndex.WorkflowCandidateIds(this, "");
            var workflows = (workflowIds != null && workflowIds.Count > 0)
                ? WorkflowExecutionMiner.Mine(Database.ReadEntries(workflowIds))
                : new List<WorkflowExecutionMiner.WorkflowInfo>();

            // Attribute EXACT warn/error counts to each operation by counting the lines whose timestamp
            // falls in the operation's [Start,End] window (whole corpus, so worker-job errors that ran
            // during the operation are attributed to it). This is precise - unlike a chart-bucket
            // approximation, which over-attributes and, for sub-bucket ops, hands adjacent operations the
            // SAME bucket's counts. Consecutive ops don't share a boundary tick, so no double-counting.
            PopulateOperationSeverityCounts(workflows);

            var ctx = new Insights.InsightContext(summary, fileDisplay, jobs, patterns, timeline, workflows);

            // GC-pressure signal: the actual "percentage time" is masked out of pattern templates, so
            // read the GC-monitor entries (folded into GC patterns) and record the peak percentage and
            // max cumulative total-time. Bounded to the GC pattern buckets; skipped if none present.
            PopulateGcStats(ctx, patterns);

            // Worker-watchdog timeouts: the timeout value (N seconds) is masked out of the folded
            // pattern template, so read the timeout entries and parse + aggregate the seconds (total and
            // per worker job) for the worker-timeout insight. Skipped if no such lines exist.
            PopulateWorkerTimeoutStats(ctx, patterns);

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
                new Insights.OutOfMemoryDetector(),
                new Insights.GcPressureDetector(thresholds),
                new Insights.DiskSpaceDetector(),
                new Insights.ThrottlingDetector(thresholds),
            };
            // Append user-supplied scripted detectors (Configuration/InsightScripts/*.js). Reloaded each
            // Analyze so edits are picked up without a restart; failures are logged and skipped.
            detectors.AddRange(Insights.InsightScriptLoader.LoadDetectors());

            var engine = new Insights.InsightEngine(detectors);

            // Built-in detectors produce trusted, compiler-built queries, but validate anyway so a bad
            // one degrades gracefully (jump disabled) rather than erroring on click - cheap (parse only).
            return engine.Run(ctx, q => SearchIndex.TryValidateQuery(q));
        }

        /// <summary>
        /// Fills each operation's <see cref="WorkflowExecutionMiner.OperationInfo.WarnCount"/> /
        /// ErrorCount with the EXACT number of WARN/ERROR lines whose timestamp falls in the operation's
        /// [Start,End] window (whole corpus). One summary pass per operation over a narrow time range;
        /// operations with no measurable window (e.g. incomplete, End &lt;= Start) are left at 0. This is
        /// what lets the workflow insight say "this operation is where the errors were" precisely.
        /// </summary>
        private void PopulateOperationSeverityCounts(IList<WorkflowExecutionMiner.WorkflowInfo> workflows)
        {
            if (workflows == null) return;
            foreach (var wf in workflows)
            {
                if (wf?.Operations == null) continue;
                foreach (var op in wf.Operations)
                {
                    if (op == null || op.End <= op.Start) continue; // no measurable window
                    string q = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "timestamp:[{0} TO {1}]", op.Start.Ticks, op.End.Ticks);
                    var s = SearchIndex.SummarizeFilteredSet(q);
                    op.WarnCount = s.Warn;
                    op.ErrorCount = s.Error;
                }
            }
        }

        // Matches Nuix GC-monitor lines: "GC PS MarkSweep: collections:N, total time:Xs, percentage time:P%".
        // total time may be "0s", "12s", "1m 3s" etc.; we capture the leading seconds-ish number loosely.
        private static readonly System.Text.RegularExpressions.Regex GcPercentRegex =
            new System.Text.RegularExpressions.Regex(@"percentage time:\s*([0-9]+(?:\.[0-9]+)?)\s*%",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex GcTotalSecRegex =
            new System.Text.RegularExpressions.Regex(@"total time:\s*([0-9]+(?:\.[0-9]+)?)\s*s\b",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Populates <see cref="Insights.InsightContext.GcPeakPercent"/> / GcMaxTotalSeconds by reading
        /// the GC-monitor entries (identified by GC-pattern buckets) and parsing their "percentage time"
        /// and "total time" values, which the pattern templates mask away. No-op if no GC lines exist.
        /// </summary>
        private void PopulateGcStats(Insights.InsightContext ctx, IList<LogSearchIndex.LogPattern> patterns)
        {
            if (ctx == null || patterns == null) return;

            // Collect ids from patterns that look like GC-monitor lines.
            var gcIds = new List<long>();
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p.Template)) continue;
                bool looksGc = p.Template.IndexOf("GC PS", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || p.Template.IndexOf("percentage time", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (looksGc && p.Ids != null) gcIds.AddRange(p.Ids);
            }
            if (gcIds.Count == 0) return;

            double peakPct = 0; double maxTotalSec = 0; bool any = false;
            foreach (var entry in Database.ReadEntries(gcIds))
            {
                if (entry?.Content == null) continue;
                var mp = GcPercentRegex.Match(entry.Content);
                if (mp.Success && double.TryParse(mp.Groups[1].Value,
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    any = true;
                    if (pct > peakPct) peakPct = pct;
                }
                var mt = GcTotalSecRegex.Match(entry.Content);
                if (mt.Success && double.TryParse(mt.Groups[1].Value,
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                {
                    if (sec > maxTotalSec) maxTotalSec = sec;
                }
            }
            if (any)
            {
                ctx.GcPeakPercent = peakPct;
                ctx.GcMaxTotalSeconds = maxTotalSec;
            }
        }

        // Nuix Worker Watchdog line: "Worker has not progressed within timeout of: 600 seconds, current
        // item: (<id>: <mimetype-path>)". Capture the timeout seconds; the folded pattern masks it as "#".
        private static readonly System.Text.RegularExpressions.Regex WorkerTimeoutRegex =
            new System.Text.RegularExpressions.Regex(
                @"has not progressed within timeout of:\s*([0-9]+(?:\.[0-9]+)?)\s*seconds",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Populates <see cref="Insights.InsightContext.WorkerTimeouts"/> by reading the worker-watchdog
        /// timeout entries (identified by the patterns that carry the timeout phrase) and parsing each
        /// "timeout of: N seconds" value. Aggregates the count and summed seconds corpus-wide and per
        /// worker job (job id from the file path, same key as <see cref="LogSearchIndex.MineJobs"/>).
        /// No-op if no such lines exist. The timeout value is masked out of the folded template, so like
        /// the GC pass we go back to the entries for the real numbers.
        /// </summary>
        private void PopulateWorkerTimeoutStats(Insights.InsightContext ctx, IList<LogSearchIndex.LogPattern> patterns)
        {
            if (ctx == null || patterns == null) return;

            // Collect ids from patterns whose template reads as the worker-timeout line.
            var ids = new List<long>();
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p.Template)) continue;
                if (p.Template.IndexOf("has not progressed within timeout", System.StringComparison.OrdinalIgnoreCase) >= 0
                    && p.Ids != null)
                {
                    ids.AddRange(p.Ids);
                }
            }
            if (ids.Count == 0) return;

            var stats = new Insights.InsightContext.WorkerTimeoutStats();
            // Per-job accumulation keyed by the "job-<hex>" id (null path/non-worker -> "" bucket).
            var perJob = new Dictionary<string, Insights.InsightContext.WorkerTimeoutStats.JobTimeout>(
                System.StringComparer.OrdinalIgnoreCase);
            // Distinct worker subfolders (e.g. "engine 02b/worker-2") that timed out, per job - so the
            // insight can express stall time PER WORKER. Workers run in parallel, so summing every
            // worker's stall and dividing by the job's wall-clock runtime is apples-to-oranges (it can
            // exceed 100%); dividing the sum by the worker count gives a comparable per-worker figure.
            var workersByJob = new Dictionary<string, HashSet<string>>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var entry in Database.ReadEntries(ids))
            {
                if (entry?.Content == null) continue;
                var m = WorkerTimeoutRegex.Match(entry.Content);
                if (!m.Success) continue;
                if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var secs))
                {
                    continue;
                }

                stats.TotalCount++;
                stats.TotalSeconds += secs;

                string jobId = JobIdExtractor.Extract(entry.FilePath) ?? "";
                if (!perJob.TryGetValue(jobId, out var jt))
                {
                    jt = new Insights.InsightContext.WorkerTimeoutStats.JobTimeout { JobId = jobId };
                    perJob[jobId] = jt;
                }
                jt.Count++;
                jt.Seconds += secs;

                // Track which distinct worker produced this timeout (worker/restart subfolder label).
                string worker = JobIdExtractor.WorkerLabel(entry.FilePath);
                if (!string.IsNullOrEmpty(worker))
                {
                    if (!workersByJob.TryGetValue(jobId, out var set))
                    {
                        set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        workersByJob[jobId] = set;
                    }
                    set.Add(worker);
                }
            }

            if (stats.TotalCount == 0) return; // matched patterns but no parseable timeout lines

            foreach (var jt in perJob.Values)
            {
                jt.WorkerCount = (workersByJob.TryGetValue(jt.JobId, out var set)) ? set.Count : 0;
            }
            stats.ByJob = perJob.Values.OrderByDescending(j => j.Seconds).ToList();
            ctx.WorkerTimeouts = stats;
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
        /// Among a specific set of entry ids, returns the id whose event time is the latest at or before
        /// <paramref name="ticks"/> (i.e. "round backwards" within that set). Falls back to the earliest
        /// id in the set when all of them are after <paramref name="ticks"/>. Used to park the grid at a
        /// workflow operation's start after drilling to the workflow's entries. Null if the set is empty.
        /// </summary>
        public long? FindEntryIdAtOrBeforeInIds(IEnumerable<long> ids, long ticks)
        {
            if (ids == null) return null;
            long? bestBefore = null; long bestBeforeTicks = long.MinValue;
            long? earliest = null; long earliestTicks = long.MaxValue;
            foreach (var entry in Database.ReadEntries(ids))
            {
                if (entry == null) continue;
                long t = entry.TimeStamp.Ticks;
                if (t <= ticks && t > bestBeforeTicks) { bestBeforeTicks = t; bestBefore = entry.ID; }
                if (t < earliestTicks) { earliestTicks = t; earliest = entry.ID; }
            }
            return bestBefore ?? earliest;
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
