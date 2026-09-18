using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class NuixLogEntry
    {
        public long ID { get; set; }
        public int LineNumber { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public DateTime TimeStamp { get; set; }
        public string Channel { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string Level { get; set; }
        public string Source { get; set; }
        public string Content { get; set; }
        public string ContentBrief
        {
            get
            {
                string result = Content.Replace("\n", " ").Replace("\r", "");
                if (result.Length > 500)
                {
                    result = result.Substring(0, 499) + "....";
                }
                return result;
            }
        }

        public IEnumerable<string> Flags { get; set; }

        /// <summary>
        /// Elapsed time since the PREVIOUS row in the current (time-sorted) filtered view - i.e. the
        /// gap to the chronologically preceding matched entry, NOT necessarily the adjacent raw log
        /// line. Null for the first row of the result set. Computed when a page is fetched
        /// (see NuixLogEntryItemProvider.FetchRange). Drives the grid's time-gap visual cue.
        /// </summary>
        public TimeSpan? GapToPrevious { get; set; }

        public NuixLogEntry() {
            Flags = new String[] { };
        }

        /// <summary>
        /// Generates a string from the constiuent pieces that should represent (hopefully) the original
        /// log line.  Used to export a subset of log entries.
        /// </summary>
        /// <returns></returns>
        public string ToLogLine()
        {
            string zoneOffset = TimeStamp.ToString("zzz").Replace(":", "");
            return TimeStamp.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + zoneOffset + " [" + Channel + "] " + Elapsed.TotalMilliseconds + " " +
                Level.PadRight(5,' ') + " " + Source + " - " + Content;
        }
    }
}
