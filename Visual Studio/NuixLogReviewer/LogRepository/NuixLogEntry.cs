using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class NuixLogEntry : INotifyPropertyChanged
    {
        public long ID { get; set; }
        public int LineNumber { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }

        /// <summary>
        /// Human-readable ORIGIN for display when this entry's log was extracted from an archive: the
        /// real source archive path plus the inner path (e.g. "C:\logs\support.zip \u2192 gateway/nuix.log").
        /// Null for loose files on disk, in which case the UI falls back to <see cref="FilePath"/>. Set
        /// when entries are materialized (see LogDatabase.ReadEntries) from the load-time origin map, so
        /// the grid's File tooltip shows where the data really came from, not the throwaway temp path.
        /// </summary>
        public string OriginLabel { get; set; }

        /// <summary>
        /// The path shown to the user for this entry's source: the archive <see cref="OriginLabel"/> when
        /// the log came from an archive, otherwise the real on-disk <see cref="FilePath"/>. Bound by the
        /// grid's File-column tooltip so archived logs show their origin rather than the temp extract path.
        /// </summary>
        public string DisplayPath => string.IsNullOrEmpty(OriginLabel) ? FilePath : OriginLabel;
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

        // --- Find-within-results indicator ---------------------------------------------------------
        // Whether this row matches the current "Find within results" term. Drives the leading
        // colored-circle indicator column. Set live (on the UI thread) as the find pass evaluates the
        // current set, so it must notify - realized rows are already bound when the find runs, unlike
        // GapToPrevious which is stamped before binding. Cleared when find is cleared.
        private bool _matchHighlight;
        public bool MatchHighlight
        {
            get { return _matchHighlight; }
            set
            {
                if (_matchHighlight != value)
                {
                    _matchHighlight = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchHighlight)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

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
