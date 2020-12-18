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
            if (filenameIdCache.ContainsKey(fileName))
            {
                return filenameIdCache[fileName];
            }
            else
            {
                FlushAllBatchInserters();
                ExecuteNonQuery("INSERT INTO FileName (Value) VALUES (@filename)", new NamedValue("@filename", fileName));
                long id = ExecuteScalar<long>("SELECT ID FROM FileName WHERE Value == @filename", new NamedValue("@filename", fileName));
                filenameIdCache[fileName] = id;
                ReinitializeAllBatchInserters();
                return id;
            }
        }

        public long GetLevelID(string level)
        {
            if (levelIdCache.ContainsKey(level))
            {
                return levelIdCache[level];
            }
            else
            {
                FlushAllBatchInserters();
                ExecuteNonQuery("INSERT INTO Level (Value) VALUES (@level)", new NamedValue("@level", level));
                long id = ExecuteScalar<long>("SELECT ID FROM Level WHERE Value == @level", new NamedValue("@level", level));
                levelIdCache[level] = id;
                ReinitializeAllBatchInserters();
                return id;
            }
        }

        public long GetSourceID(string source)
        {
            if (sourceIdCache.ContainsKey(source))
            {
                return sourceIdCache[source];
            }
            else
            {
                FlushAllBatchInserters();
                ExecuteNonQuery("INSERT INTO Source (Value) VALUES (@source)", new NamedValue("@source", source));
                long id = ExecuteScalar<long>("SELECT ID FROM Source WHERE Value == @source", new NamedValue("@source", source));
                sourceIdCache[source] = id;
                ReinitializeAllBatchInserters();
                return id;
            }
        }

        public long GetChannelID(string channel)
        {
            if (channelIdCache.ContainsKey(channel))
            {
                return channelIdCache[channel];
            }
            else
            {
                FlushAllBatchInserters();
                ExecuteNonQuery("INSERT INTO Channel (Value) VALUES (@channel)", new NamedValue("@channel", channel));
                long id = ExecuteScalar<long>("SELECT ID FROM Channel WHERE Value == @channel", new NamedValue("@channel", channel));
                channelIdCache[channel] = id;
                ReinitializeAllBatchInserters();
                return id;
            }
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

            return ExecuteReader<NuixLogEntry>(query, new Func<SQLiteDataReader, NuixLogEntry>(reader =>
            {
                string content = reader["Content"] as string;
                NuixLogEntry entry = new NuixLogEntry()
                {
                    ID = (long)reader["ID"],
                    LineNumber = (int)(long)reader["LineNumber"],
                    FilePath = reader["FileName"].ToString(),
                    FileName = Path.GetFileName(reader["FileName"].ToString()) + "(" + ((long)reader["FileID"]).ToString() + ")",
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
