using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Class which provides methods for interacting with SQLite
    /// </summary>
    public class SQLiteRepo
    {
        public string DataSource { get; private set; }

        public SQLiteRepo(string dataSource)
        {
            DataSource = dataSource;
            InitializeDatabase();
        }

        /// <summary>
        /// Gets embbeded resource SQL
        /// </summary>
        /// <param name="name">Fully qualified embedded resource name.</param>
        /// <returns>The contents of the embedded resource.</returns>
        public string GetEmbeddedSQL(string name)
        {
            //var debugnames = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            Assembly sourceAssembly = Assembly.GetAssembly(typeof(SQLiteRepo));
            using (StreamReader sr = new StreamReader(sourceAssembly.GetManifestResourceStream(name)))
            {
                return sr.ReadToEnd();
            }
        }

        /// <summary>
        /// Provided by derived class to initialize the database.
        /// </summary>
        protected virtual void InitializeDatabase()
        {

        }

        /// <summary>
        /// Creates a connection string for getting SQLite to open the DB file at DataSource.
        /// </summary>
        /// <returns></returns>
        public virtual string GetConnectionString()
        {
            SQLiteConnectionStringBuilder builder = new SQLiteConnectionStringBuilder();
            builder.DataSource = DataSource;
            builder.JournalMode = SQLiteJournalModeEnum.Off;
            builder.CacheSize = 10000;
            builder.PageSize = 4096;
            builder.Pooling = false;

            builder.SyncMode = SynchronizationModes.Off;

            return builder.ToString();
        }

        /// <summary>
        /// Create a connection and opens it.
        /// </summary>
        /// <returns>The opened connection</returns>
        public SQLiteConnection GetOpenConnection()
        {
            SQLiteConnection conn = new SQLiteConnection(GetConnectionString());
            conn.Open();
            return conn;
        }

        /// <summary>
        /// Executes a SQL query which returns a single value (for example a COUNT query)
        /// </summary>
        /// <typeparam name="T">Datatype of the resulting value</typeparam>
        /// <param name="sql">The SQL query to execute</param>
        /// <param name="values">Optional values to bind to the statement</param>
        /// <returns></returns>
        public T ExecuteScalar<T>(string sql, params NamedValue[] values)
        {
            T result = default(T);
            using (SQLiteConnection connection = GetOpenConnection())
            {
                result = ExecuteScalar<T>(connection, sql, values);
            }
            return result;
        }

        /// <summary>
        /// Executes a SQL query which returns a single value (for example a COUNT query)
        /// </summary>
        /// <typeparam name="T">Datatype of the resulting value</typeparam>
        /// <param name="connection">The connection to perform the query over</param>
        /// <param name="sql">The SQL query to execute</param>
        /// <param name="values">Optional values to bind to the statement</param>
        /// <returns></returns>
        public T ExecuteScalar<T>(SQLiteConnection connection, string sql, params NamedValue[] values)
        {
            using (SQLiteCommand command = new SQLiteCommand(sql, connection))
            {
                if (values != null)
                {
                    foreach (var parameter in values)
                    {
                        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                    }
                }
                var scalarValue = command.ExecuteScalar();
                return (T)scalarValue;
            }
        }

        /// <summary>
        /// Executes a SQL statement that doesn't return results (like DB create, INSERT, etc)
        /// </summary>
        /// <param name="sql">The SQL statement to execute</param>
        /// <param name="values">Optional values to bind to the statement</param>
        public void ExecuteNonQuery(string sql, params NamedValue[] values)
        {
            using (SQLiteConnection connection = GetOpenConnection())
            {
                ExecuteNonQuery(connection, sql, values);
            }
        }

        /// <summary>
        /// Executes a SQL statement that doesn't return results (like DB create, INSERT, etc)
        /// </summary>
        /// <param name="connection">The connection to execute the statement on</param>
        /// <param name="sql">The SQL statement to execute</param>
        /// <param name="values">Optional values to bind to the statement</param>
        public void ExecuteNonQuery(SQLiteConnection connection, string sql, params NamedValue[] values)
        {
            using (SQLiteCommand command = new SQLiteCommand(sql, connection))
            {
                if (values != null)
                {
                    foreach (var parameter in values)
                    {
                        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                    }

                    command.ExecuteNonQuery();
                }
            }
        }

        public IEnumerable<T> ExecuteReader<T>(string sql, Func<SQLiteDataReader, T> readerAction, params NamedValue[] values)
        {
            using (SQLiteConnection connection = GetOpenConnection())
            {
                foreach (var item in ExecuteReader<T>(connection, sql, readerAction, values))
                {
                    yield return item;
                }
            }
        }

        public IEnumerable<T> ExecuteReader<T>(SQLiteConnection connection, string sql, Func<SQLiteDataReader, T> readerAction, params NamedValue[] values)
        {
            using (SQLiteCommand command = new SQLiteCommand(sql, connection))
            {
                if (values != null)
                {
                    foreach (var parameter in values)
                    {
                        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                    }
                }

                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        yield return readerAction.Invoke(reader);
                    }
                }
            }
        }

        private List<SQLiteBatchInserter> batchInserters = new List<SQLiteBatchInserter>();

        /// <summary>
        /// Creates a SQLiteBatchInserter instance against this database and tracks is for future
        /// calls to FlushAllBatchInserters() and ReinitializeAllBatchInserters()
        /// </summary>
        /// <returns></returns>
        public SQLiteBatchInserter CreateBatchInserter()
        {
            SQLiteBatchInserter result = new SQLiteBatchInserter(this);
            batchInserters.Add(result);
            return result;
        }

        /// <summary>
        /// Causes this instance to drop its reference to all the SQLiteBatchInserter instances it
        /// has handed out so they can be garbage collected and so they no longer are included in
        /// FlushAllBatchInserters() and ReinitializeAllBatchInserters() calls.
        /// </summary>
        /// <param name="batchInserter"></param>
        public void ReleaseBatchInserter(SQLiteBatchInserter batchInserter)
        {
            batchInserters.Remove(batchInserter);
        }

        /// <summary>
        /// Instructs all SQLiteBatchInserter instances handed out by this instance to
        /// commit their transaction and close it. Since SQLite locks the whole database during
        /// transaction, this allows for other writes to sneak in quickly.
        /// </summary>
        public void FlushAllBatchInserters()
        {
            foreach (var bi in batchInserters)
            {
                bi.Flush();
            }
        }

        /// <summary>
        /// Instructs all SQLiteBatchInserter instances handed out by this instance that they
        /// can reinitialize their transaction, see FlushAllBatchInserts method for why.
        /// </summary>
        public void ReinitializeAllBatchInserters()
        {
            foreach (var bi in batchInserters)
            {
                bi.Reinitialize();
            }
        }
    }
}
