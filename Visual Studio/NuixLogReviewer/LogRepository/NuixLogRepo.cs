using Lucene.Net.Documents;
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

            // Drop index and we will rebuild all at once after we have pulled in all the records
            pb.BroadcastStatus("Dropping database indexes...");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_EntryID;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_TimeStamp;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_LineNumber;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_FilenameID;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_ChannelID;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_LevelID;");
            Database.ExecuteNonQuery("DROP INDEX IF EXISTS IDX_SourceID;");

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
            // we dropped the index and now we rebuild it
            pb.BroadcastStatus("Rebuilding database indexes...");
            Database.ExecuteNonQuery("CREATE INDEX IDX_EntryID ON LogEntry (ID);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_TimeStamp ON LogEntry (TimeStamp);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_LineNumber ON LogEntry (LineNumber);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_FilenameID ON FileName (ID);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_ChannelID ON Channel (ID);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_LevelID ON Level (ID);");
            Database.ExecuteNonQuery("CREATE INDEX IDX_SourceID ON Level (ID);");
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
            Task[] indexers = new Task[indexingConcurrency];
            for (int i = 0; i < indexingConcurrency; i++)
            {
                Task indexConsumer = new Task(new Action(() =>
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

                    while (true)
                    {
                        NuixLogEntry entry = toIndex.Take();
                        if (entry == null) { break; }

                        // Push to Lucene
                        idField.SetLongValue(entry.ID);
                        lineField.SetLongValue(entry.LineNumber);
                        channelField.SetValue(entry.Channel);
                        levelField.SetValue(entry.Level);
                        sourceField.SetValue(entry.Source);
                        contentField.SetValue(entry.Content);
                        long date = (entry.TimeStamp.Year * 10000) + (entry.TimeStamp.Month * 100) + entry.TimeStamp.Day;
                        dateField.SetLongValue(date);
                        flagsField.SetValue(String.Join(" ", entry.Flags));

                        SearchIndex.IndexLogEntry(doc);
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

        public LogEntrySearchResponse Search(string query)
        {
            IList<long> ids = SearchIndex.Search(this, query);
            LogEntrySearchResponse result = new LogEntrySearchResponse(new NuixLogEntryItemProvider()
            {
                Ids = ids, //Database.SortIds(ids),
                SourceRepository = this
            }, 1000);

            // For the given search result we want to be able to additionally report how many
            // are each log level
            if (String.IsNullOrWhiteSpace(query))
            {
                result.InfoEntryCount = SearchIndex.Count(this, "level:info");
                result.WarnEntryCount = SearchIndex.Count(this, "level:warn");
                result.ErrorEntryCount = SearchIndex.Count(this, "level:error");
                result.DebugEntryCount = SearchIndex.Count(this, "level:debug");
            }
            else
            {
                result.InfoEntryCount = SearchIndex.Count(this, String.Format("({0}) AND level:info", query));
                result.WarnEntryCount = SearchIndex.Count(this, String.Format("({0}) AND level:warn", query));
                result.ErrorEntryCount = SearchIndex.Count(this, String.Format("({0}) AND level:error", query));
                result.DebugEntryCount = SearchIndex.Count(this, String.Format("({0}) AND level:debug", query));
            }

            return result;
        }

        public void DisposeRepo()
        {
            if (!RepoDisposed)
            {
                Directory.Delete(RepoDirectory, true);
                RepoDisposed = true;
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
