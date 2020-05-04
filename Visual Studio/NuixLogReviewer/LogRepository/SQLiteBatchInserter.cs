using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class SQLiteBatchInserter
    {
        public SQLiteRepo Repository { get; private set; }
        public SQLiteConnection Connection { get; private set; }
        public SQLiteTransaction Transaction { get; private set; }
        public SQLiteCommand Command { get; private set; }
        public int CommitFrequency { get; private set; }

        private int pendingCommit = 0;
        private Dictionary<string, SQLiteParameter> paramLookup = new Dictionary<string, SQLiteParameter>(StringComparer.OrdinalIgnoreCase);

        public SQLiteBatchInserter(SQLiteRepo repo, int commitFrequency = 10000)
        {
            Repository = repo;
            CommitFrequency = commitFrequency;
        }

        public void Begin(string sql)
        {
            Connection = Repository.GetOpenConnection();
            Transaction = Connection.BeginTransaction();
            Command = new SQLiteCommand(sql, Connection, Transaction);
            paramLookup.Clear();
            pendingCommit = 0;
        }

        public object this[string name]
        {
            get { return paramLookup[name].Value; }
            set
            {
                if (!paramLookup.ContainsKey(name))
                {
                    paramLookup.Add(name, Command.Parameters.AddWithValue(name, value));
                }
                else
                {
                    paramLookup[name].Value = value;
                }
            }
        }

        public void Insert()
        {
            Command.ExecuteNonQuery();
            pendingCommit++;
            if (pendingCommit > CommitFrequency)
            {
                Flush();
                Reinitialize();
            }
        }

        public void Flush()
        {
            Transaction.Commit();
            Transaction.Dispose();
            pendingCommit = 0;
        }

        public void Reinitialize()
        {
            if (pendingCommit > 0) Flush();
            Transaction = Connection.BeginTransaction();
            Command.Transaction = Transaction;
        }

        public void Complete()
        {
            Command.Dispose();
            Transaction.Commit();
            Transaction.Dispose();
            Connection.Dispose();
        }
    }
}
