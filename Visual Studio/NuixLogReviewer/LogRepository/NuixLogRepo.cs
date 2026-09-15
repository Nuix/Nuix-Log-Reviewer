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

            long overallRecordCount = 0;

            // Open things up for writing to the index
            SearchIndex.BeginWrite();

            foreach (var logFile in logFiles)
            {
                overallRecordCount = LoadLogFile(logFile, overallRecordCount, pb);
            }

            // Tell search index to close
            SearchIndex.EndWrite();

            // SQLite is faster building whole index at once rather than on each insert, so earlier
            // we dropped the LogEntry indexes and now we rebuild them.
            pb.BroadcastStatus("Rebuilding database indexes...");
            Database.ExecuteNonQuery("CREATE INDEX IDX_EntryID ON LogEntry (ID);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_TimeStamp ON LogEntry (TimeStamp);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_LineNumber ON LogEntry (LineNumber);");
        }

        /// <summary>
        /// This should only be called by public method LoadlogFiles since it takes care of index drop and rebuild.
        /// </summary>
        /// <param name="logFile">Path to a log file to load.</param>
        /// <param name="pb">ProgressBroadcaster which will received progress updates, can be null.</param>
        private long LoadLogFile(string logFile, long startingRecordCount, ProgressBroadcaster pb = null)
        {
            FileInfo logFileInfo = new FileInfo(logFile);
            if (logFileInfo.Length < 1)
            {
                // Skip 0 length files
                return startingRecordCount;
            }

            int indexingConcurrency = 8;

            pb.BroadcastStatus("Loading from " + logFile);

            // Can be tricky to do batch insert and get each new record's ID, so instead we query database for current
            // highest ID value and increment and assign IDs here rather than letting DB auto increment do the job.
            long nextId = Database.GetHighestLogEntryID();

            NuixLogReader reader = new NuixLogReader(logFile);

            SQLiteBatchInserter batchInserter = Database.CreateBatchInserter(5000);
            batchInserter.Begin(Database.GetEmbeddedSQL("NuixLogReviewer.LogRepository.InsertLogEntry.sqlite"));

            // Used for progress updates
            object locker = new object();
            long recordCount = startingRecordCount;

            List<IEntryClassifier> classifiers = getAllClassifiers();

            BlockingCollection<NuixLogEntry> toInsert = new BlockingCollection<NuixLogEntry>();
            BlockingCollection<NuixLogEntry> toClassify = new BlockingCollection<NuixLogEntry>();
            BlockingCollection<NuixLogEntry> toIndex = new BlockingCollection<NuixLogEntry>();

            // ==== Task Dedicated to Pulling Entries from Source ====
            Task readerConsumer = new Task(new Action(() =>
            {
                foreach (var entry in reader)
                {
                    toClassify.Add(entry);
                }

                // Signal that was the last one
                toClassify.Add(null);

            }), TaskCreationOptions.LongRunning);

            // ==== Classify Log Entries ====
            Task classificationTask = new Task(new Action(() =>
            {
                while (true)
                {
                    NuixLogEntry entry = toClassify.Take();
                    if (entry == null) { break; }

                    // Give each classifier a chance to look at this entry and provide flag
                    // values to be assigned to the entry.
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

                // Signal that was the last one
                toInsert.Add(null);
            }), TaskCreationOptions.LongRunning);

            // ==== Task Dedicated to Inserting to SQLite Database ====
            Task dbConsumer = new Task(new Action(() =>
            {
                DateTime lastProgress = DateTime.Now;

                while (true)
                {
                    NuixLogEntry entry = toInsert.Take();
                    if (entry == null) { break; }

                    nextId++;

                    // Push to SQLite database
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

                    // Periodically report progress
                    if ((DateTime.Now - lastProgress).TotalMilliseconds >= 500)
                    {
                        lock (this) { pb.BroadcastProgress(recordCount); }
                        lastProgress = DateTime.Now;
                    }

                    toIndex.Add(entry);
                }

                // Let each indexing task know there are no more to index
                for (int i = 0; i < indexingConcurrency; i++)
                {
                    toIndex.Add(null);
                }
            }), TaskCreationOptions.LongRunning);

            // ==== Series of Tasks Dedicated to Adding Entries to Lucene Index ====
            // All Lucene Document construction lives in LogSearchIndex.IndexLogEntry(entry) so the
            // write-time field configuration stays in one place and can't drift from the query-time
            // analyzer configuration. IndexWriter.AddDocument is thread-safe, so we can fan this out.
            Task[] indexers = new Task[indexingConcurrency];
            for (int i = 0; i < indexingConcurrency; i++)
            {
                Task indexConsumer = new Task(new Action(() =>
                {
                    while (true)
                    {
                        NuixLogEntry entry = toIndex.Take();
                        if (entry == null) { break; }

                        SearchIndex.IndexLogEntry(entry);
                    }

                    pb.BroadcastProgress(recordCount);
                }), TaskCreationOptions.LongRunning);
                indexers[i] = indexConsumer;
                indexConsumer.Start();
            }

            readerConsumer.Start();
            classificationTask.Start();
            dbConsumer.Start();

            // Wait for them all to finish up
            Task.WaitAll(readerConsumer, classificationTask, dbConsumer);

            pb.BroadcastStatus("Waiting for indexing to complete...");
            Task.WaitAll(indexers);

            // Report final progress
            pb.BroadcastProgress(recordCount);

            // Make sure batch inserter flushes any pending inserts
            batchInserter.Complete();

            Database.ReleaseBatchInserter(batchInserter);

            toClassify.Dispose();
            toInsert.Dispose();
            toIndex.Dispose();

            return recordCount;
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
            var summary = SearchIndex.SummarizeFilteredSet(query);

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

        // Use reflection to get an instance of all defined classifiers (classes that implement IEntryClassifier)
        private List<IEntryClassifier> getAllClassifiers()
        {
            var iEntryClassifierType = typeof(IEntryClassifier);
            var classifierTypes = Assembly.GetExecutingAssembly().GetExportedTypes()
                .Where(t => !t.IsInterface && iEntryClassifierType.IsAssignableFrom(t));
            List<IEntryClassifier> result = new List<IEntryClassifier>();
            foreach (var classifierType in classifierTypes)
            {
                result.Add((IEntryClassifier)Activator.CreateInstance(classifierType));
            }
            return result;
        }
    }
}
