using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class LogDatabase : SQLiteRepo
    {
        private Dictionary<string, long> filenameIdCache = new Dictionary<string, long>();
        private Dictionary<string, long> levelIdCache = new Dictionary<string, long>();
        private Dictionary<string, long> sourceIdCache = new Dictionary<string, long>();
        private Dictionary<string, long> channelIdCache = new Dictionary<string, long>();

        // Full path => shortest-unique display tail (same scheme as the Files tab), so the grid's File
        // column shows a meaningful disambiguated name (e.g. "worker3/nuix.log") instead of an opaque
        // "nuix.log(42)". Built lazily from the distinct loaded paths; rebuilt if the file set grows.
        private Dictionary<string, string> _fileDisplayNames;
        private int _fileDisplayNamesBuiltForCount = -1;

        /// <summary>
        /// Returns (building if needed) the full-path =&gt; short-unique-tail display map across all
        /// distinct loaded file paths (read from the FileName lookup table, the source of truth). Uses
        /// the same <see cref="FileDisplay.ShortUniqueNames"/> scheme as the Files tab so the grid's File
        /// column matches it. Rebuilt only when the number of distinct files changes (a small set).
        /// </summary>
        public IReadOnlyDictionary<string, string> FileDisplayNames()
        {
            int fileCount = (int)ExecuteScalar<long>("SELECT COUNT(*) FROM FileName");
            if (_fileDisplayNames == null || _fileDisplayNamesBuiltForCount != fileCount)
            {
                var paths = ExecuteReader<string>(
                    "SELECT Value FROM FileName",
                    r => r["Value"].ToString()).ToList();
                _fileDisplayNames = FileDisplay.ShortUniqueNames(paths);
                _fileDisplayNamesBuiltForCount = fileCount;
            }
            return _fileDisplayNames;
        }

        public object Database { get; private set; }

        public LogDatabase(string dataSource) : base(dataSource)
        {
        }

        protected override void InitializeDatabase()
        {
            ExecuteNonQuery(GetEmbeddedSQL("NuixLogReviewer.LogRepository.InitializeDatabase.sqlite"));
        }

        public long GetHighestLogEntryID()
        {
            if (TotalRecords == 0) { return 0; }
            else { return ExecuteScalar<long>("SELECT MAX(ID) FROM LogEntry"); }
        }

        public long TotalRecords
        {
            get { return ExecuteScalar<long>("SELECT COUNT(*) FROM LogEntry"); }
        }

        public long GetFilenameID(string fileName)
        {
            return GetOrCreateLookupId("FileName", fileName, filenameIdCache);
        }

        public long GetLevelID(string level)
        {
            return GetOrCreateLookupId("Level", level, levelIdCache);
        }

        public long GetSourceID(string source)
        {
            return GetOrCreateLookupId("Source", source, sourceIdCache);
        }

        public long GetChannelID(string channel)
        {
            return GetOrCreateLookupId("Channel", channel, channelIdCache);
        }

        // Lookup-table names are fixed, internal constants (never user input), so building the
        // SQL with the table name is safe from injection. The Value is always parameterized.
        // We use a plain INSERT ... RETURNING on the (cache-guaranteed) new value so the
        // AUTOINCREMENT sequence stays gap-free; a UNIQUE-constraint violation only happens on
        // the defensive path (value already present), where we fall back to a SELECT.
        private static readonly Dictionary<string, string> LookupInsertSql = new Dictionary<string, string>
        {
            ["FileName"] = "INSERT INTO FileName (Value) VALUES (@value) RETURNING ID;",
            ["Channel"]  = "INSERT INTO Channel (Value) VALUES (@value) RETURNING ID;",
            ["Level"]    = "INSERT INTO Level (Value) VALUES (@value) RETURNING ID;",
            ["Source"]   = "INSERT INTO Source (Value) VALUES (@value) RETURNING ID;",
        };

        private static readonly Dictionary<string, string> LookupSelectSql = new Dictionary<string, string>
        {
            ["FileName"] = "SELECT ID FROM FileName WHERE Value = @value;",
            ["Channel"]  = "SELECT ID FROM Channel WHERE Value = @value;",
            ["Level"]    = "SELECT ID FROM Level WHERE Value = @value;",
            ["Source"]   = "SELECT ID FROM Source WHERE Value = @value;",
        };

        /// <summary>
        /// Returns the ID for the given Value in the named lookup table, inserting a new row if it
        /// does not yet exist. Results are cached in memory so each distinct value hits the DB once.
        /// </summary>
        /// <remarks>
        /// On the normal path (fresh per-run DB, value not yet cached) this is a single
        /// INSERT ... RETURNING round-trip, kept gap-free by relying on the in-memory cache to
        /// guarantee we only insert genuinely-new values. If the value already exists (a defensive
        /// case, e.g. a re-used DB), the UNIQUE index on Value raises a constraint violation and we
        /// fall back to a SELECT. This replaces the previous approach that always did a separate
        /// INSERT then SELECT on two fresh connections.
        ///
        /// The batch inserters are still flushed around the call: this method runs on a separate
        /// connection while the main LogEntry batch inserter holds an open write transaction, and
        /// SQLite locks the whole database during a write (JournalMode=Off, Pooling=false), so a
        /// concurrent write from another connection would otherwise fail with SQLITE_BUSY. Because
        /// results are cached, the flush only happens once per distinct value.
        /// </remarks>
        private long GetOrCreateLookupId(string tableName, string value, Dictionary<string, long> cache)
        {
            if (cache.TryGetValue(value, out long cachedId))
            {
                return cachedId;
            }

            FlushAllBatchInserters();
            long id;
            try
            {
                id = ExecuteScalar<long>(LookupInsertSql[tableName], new NamedValue("@value", value));
            }
            catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Constraint)
            {
                // Value already exists (defensive path) - look up its existing ID instead.
                id = ExecuteScalar<long>(LookupSelectSql[tableName], new NamedValue("@value", value));
            }
            ReinitializeAllBatchInserters();

            cache[value] = id;
            return id;
        }

        public List<long> GetAllIds()
        {
            List<long> result =
            ExecuteReader<long>("SELECT ID FROM LogEntry ORDER BY TimeStamp ASC", (reader) =>
            {
                return (long)reader["ID"];
            }).ToList();
            //string debug = "SELECT * FROM LogEntry WHERE ID IN (" + string.Join(",", result) + ")";
            return result;
        }

        public HashSet<string> GetAllFlags()
        {
            // Should probably store this differently in database at some point

            HashSet<string> result = new HashSet<string>();
            IEnumerable<string> flagCombos = ExecuteReader<string>("SELECT DISTINCT(Flags) FROM LogEntry", (reader) =>
            {
                return (string)reader["Flags"];
            });

            foreach (var flagCombo in flagCombos)
            {
                foreach (var flag in flagCombo.Split(' '))
                {
                    if (String.IsNullOrWhiteSpace(flag)) { continue; }
                    result.Add(flag);
                }
            }

            return result;
        }

        /// <summary>
        /// Sorts list of IDs using database information to order them in TimeSTamp and LineNumber order
        /// </summary>
        /// <param name="unsortedIds"></param>
        /// <returns></returns>
        public IList<long> SortIds(IList<long> unsortedIds)
        {
            ExecuteNonQuery("CREATE TABLE IDList ( ID INTEGER );");

            SQLiteBatchInserter inserter = new SQLiteBatchInserter(this, 100000);
            inserter.Begin("INSERT INTO IDList (ID) VALUES (@id)");
            foreach (var id in unsortedIds)
            {
                inserter["@id"] = id;
                inserter.Insert();
            }
            inserter.Complete();

            ExecuteNonQuery("CREATE INDEX IDX_TempIDList ON IDList (ID);");

            string retrievalQuery = "SELECT ID FROM LogEntry WHERE ID IN (SELECT ID FROM IDList) ORDER BY TimeStamp ASC, LineNumber ASC";

            List<long> sortedIds =
            ExecuteReader<long>(retrievalQuery, (reader) =>
            {
                return (long)reader["ID"];
            }).ToList();

            //long[] sortedIds =
            //ExecuteReader<long>(retrievalQuery, (reader) =>
            //{
            //    return (long)reader["ID"];
            //}).ToArray();

            ExecuteNonQuery("DROP TABLE IDList;");

            return sortedIds;
        }

        public IEnumerable<NuixLogEntry> ReadEntries(IEnumerable<long> ids)
        {
            string query = null;
            if (ids == null)
            {
                query = GetEmbeddedSQL("NuixLogReviewer.LogRepository.LogEntrySelect.sqlite");
            }
            else
            {
                query = GetEmbeddedSQL("NuixLogReviewer.LogRepository.LogEntrySelectIdList.sqlite");
                String idlist = String.Join(",", ids.Select((id) => id.ToString()));
                query = String.Format(query, idlist);
            }

            // Resolve the shared short-unique display-name map once (built lazily, cached), so the grid's
            // File column shows the same disambiguated tail as the Files tab (e.g. "worker3/nuix.log")
            // rather than "nuix.log(42)".
            var display = FileDisplayNames();

            return ExecuteReader<NuixLogEntry>(query, new Func<SQLiteDataReader, NuixLogEntry>(reader =>
            {
                string content = reader["Content"] as string;
                string filePath = reader["FileName"].ToString();
                NuixLogEntry entry = new NuixLogEntry()
                {
                    ID = (long)reader["ID"],
                    LineNumber = (int)(long)reader["LineNumber"],
                    FilePath = filePath,
                    FileName = display.TryGetValue(filePath, out var shortName)
                        ? shortName
                        : Path.GetFileName(filePath),
                    TimeStamp = DateTime.FromFileTime((long)reader["TimeStamp"]),
                    Channel = reader["Channel"].ToString(),
                    Elapsed = TimeSpan.FromMilliseconds((long)reader["Elapsed"]),
                    Level = String.Intern(reader["Level"].ToString()), // Use interned string
                    Source = reader["Source"].ToString(),
                    Content = content,
                    Flags = (reader["Flags"] as string).Split(' ')
                };
                return entry;
            }));
        }
    }
}
