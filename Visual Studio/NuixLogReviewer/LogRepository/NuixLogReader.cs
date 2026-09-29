using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Reads a Nuix log line by line, parsing it into log entries.  Log entries are not simply each line in the given log file.
    /// Since a given log's message content may span multiple lines, an entry in a log file may be multiple lines.
    /// </summary>
    public class NuixLogReader : IEnumerable<NuixLogEntry>
    {
        public static Regex LineParseRegex
        {
            get; private set;
        }

        public static string TimestampExpression
        {
            // The timezone offset (e.g. "+0000") is optional: classic Nuix Workstation logs include
            // it, but Automate logs (scheduler/engine-server/engine job/init) do not. The millisecond
            // separator may be '.' (Nuix/Automate) or ',' (Derby server logs use "HH:mm:ss,fff").
            get { return @"^(?<timestamp>20\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}[.,]\d+)(?:\s+(?<tz>[\+\-]?\d{4}))?\s+"; }
        }

        public static string ChannelExpression
        {
            // Channel may itself contain nested brackets, e.g.
            // "[JMX Monitor ThreadGroup<main> Executor Pool [Thread-1]]". Use a greedy capture that
            // backtracks to the closing bracket followed by the elapsed/level structure.
            get { return @"\[(?<channel>.+)\]\s+"; }
        }

        public static string ElapsedExpression
        {
            // Elapsed (milliseconds since start) is present in classic Workstation logs but absent
            // from Automate logs, so it is optional.
            get { return @"(?:(?<elapsed>\d+)\s+)?"; }
        }

        public static string LevelExpression
        {
            get { return @"(?<level>(TRACE|DEBUG|INFO|WARN|ERROR))\s+"; }
        }

        public static string SourceExpression
        {
            get { return @"(?<source>[^\-]+)\s-\s"; }
        }

        public static string ContentExpression
        {
            get { return @"(?<content>.*$)"; }
        }

        public static string LogLineExpression
        {
            get { return TimestampExpression + ChannelExpression + ElapsedExpression + LevelExpression + SourceExpression + ContentExpression; }
        }

        static NuixLogReader()
        {
            LineParseRegex = new Regex(LogLineExpression, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        // Some Nuix log messages embed raw control characters - notably NUL (\0) used as a delimiter
        // around markers like "\0Unextractable-Object\0". An embedded NUL is treated as a C-string
        // terminator when SQLite (System.Data.SQLite) binds the value, silently truncating the stored
        // Content at the first NUL (and it isn't meaningful text anyway). Strip NUL and other C0 control
        // chars except tab (\t), newline (\n) and carriage return (\r), which multi-line content needs.
        private static readonly Regex ControlChars = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

        /// <summary>Removes embedded control chars (NUL etc.) that would truncate or corrupt stored text.</summary>
        private static string SanitizeContent(string s) =>
            string.IsNullOrEmpty(s) ? s : ControlChars.Replace(s, "");

        public string FilePath { get; private set; }

        public NuixLogReader(string filePath)
        {
            FilePath = filePath;
        }

        /// <summary>Number of leading non-blank lines sampled to detect the file's format.</summary>
        private const int FormatSampleLines = 20;

        /// <summary>
        /// Detects this file's line format by sampling its first <see cref="FormatSampleLines"/> non-blank
        /// lines and choosing whichever known format matches the most of them (see
        /// <see cref="LogLineFormats.Detect"/>). A cheap separate read of only the sample; falls back to
        /// the Nuix format when nothing matches, so unrecognized files behave as before.
        /// </summary>
        private LogLineFormat DetectFileFormat()
        {
            var sample = new List<string>(FormatSampleLines);
            try
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string l;
                    while (sample.Count < FormatSampleLines && (l = sr.ReadLine()) != null)
                    {
                        if (l.Length > 0) sample.Add(l);
                    }
                }
            }
            catch
            {
                // If sampling fails, fall through to the default (Nuix) format.
            }
            return LogLineFormats.Detect(sample);
        }

        // Anchor for deriving the calendar date of a TIME-ONLY log: the engine logs print
        // "... - DateTime UTC: 2026-09-10T17:09:48.881Z" near the top, and that SAME line carries the
        // local wall-clock time as its leading "HH:mm:ss" stamp. Pairing the two lets us recover the
        // local calendar date without shifting any displayed time (we keep wall-clock-as-written).
        private static readonly Regex TimeOnlyLeadingTime =
            new Regex(@"^(?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})[.,](?<ms>\d+)", RegexOptions.Compiled);
        private static readonly Regex DateTimeUtcAnchor =
            new Regex(@"DateTime UTC:\s*(?<iso>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)", RegexOptions.Compiled);
        // Fallback anchor: workflow lifecycle lines end with "... on M/d/yy, h:mmAM/PM" (local date+time).
        private static readonly Regex WorkflowOnDate =
            new Regex(@"\bon (?<mo>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{2}),?\s", RegexOptions.Compiled);

        /// <summary>
        /// Derives the starting CALENDAR DATE for a time-only log (one whose lines carry only HH:mm:ss).
        /// Strategy, best-first:
        ///  1) A "DateTime UTC: &lt;iso&gt;Z" line: convert that instant to the machine-local date and, if
        ///     the same line's leading local time lands on the previous/next day vs. the UTC calendar day,
        ///     nudge by ±1 so the DATE matches the wall-clock we actually display. (Robust across tz.)
        ///  2) A workflow "on M/d/yy" line (local date, matches the displayed wall-clock).
        ///  3) The file's last-write time (archive/extracted copies preserve a plausible date).
        ///  4) Today, as a last resort (keeps entries in one coherent day rather than year 0001).
        /// Only the DATE is taken from the anchor; per-line times are shown exactly as written.
        /// </summary>
        private DateTime DeriveSeedDate()
        {
            string firstLocalTime = null;
            try
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string l;
                    int scanned = 0;
                    while (scanned < 200 && (l = sr.ReadLine()) != null)
                    {
                        scanned++;
                        if (l.Length == 0) continue;
                        if (firstLocalTime == null)
                        {
                            var tm = TimeOnlyLeadingTime.Match(l);
                            if (tm.Success) firstLocalTime = tm.Value;
                        }

                        var utc = DateTimeUtcAnchor.Match(l);
                        if (utc.Success &&
                            DateTime.TryParse(utc.Groups["iso"].Value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var inst))
                        {
                            var localDate = inst.ToLocalTime().Date;
                            // Reconcile with the line's own local wall-clock hour to catch a day-boundary
                            // straddle (e.g. UTC just past midnight while local is still the prior evening).
                            var lead = TimeOnlyLeadingTime.Match(l);
                            if (lead.Success && int.TryParse(lead.Groups["h"].Value, out int localHour))
                            {
                                int utcHour = inst.Hour;
                                if (utcHour < 6 && localHour > 18) localDate = localDate.AddDays(-1);
                                else if (utcHour > 18 && localHour < 6) localDate = localDate.AddDays(1);
                            }
                            return localDate;
                        }

                        var wf = WorkflowOnDate.Match(l);
                        if (wf.Success &&
                            int.TryParse(wf.Groups["mo"].Value, out int mo) &&
                            int.TryParse(wf.Groups["d"].Value, out int dd) &&
                            int.TryParse(wf.Groups["y"].Value, out int yy))
                        {
                            try { return new DateTime(2000 + yy, mo, dd); } catch { /* bad date, keep scanning */ }
                        }
                    }
                }
            }
            catch { /* fall through to file time / today */ }

            try { return File.GetLastWriteTime(FilePath).Date; } catch { }
            return DateTime.Today;
        }

        public IEnumerator<NuixLogEntry> GetEnumerator()
        {
            FileInfo fileInfo = new FileInfo(FilePath);
            if (fileInfo.Length == 0)
            {
                yield break;
            }

            // Detect this file's line format once by sampling its first non-blank lines, so different
            // producers (Nuix/Automate/Derby vs. Adaptive container logs) never cross-match. The whole
            // file is then parsed with the winning format.
            LogLineFormat format = DetectFileFormat();

            // Time-only formats (Automate engine per-job/init logs) carry no date on their lines; derive
            // the file's starting calendar date once, then rebase each entry onto it (advancing across
            // midnight). Non-date-context formats leave these unused.
            bool needDate = format.RequiresDateContext;
            DateTime runningDate = needDate ? DeriveSeedDate() : default;
            TimeSpan lastTimeOfDay = TimeSpan.MinValue;

            int streamReaderBufferSize = 1024 * 1024 * 5;

            // Open with FileShare.ReadWrite | Delete so we can read a log that an ACTIVE Nuix session is
            // still writing to (and may rename/roll). Without write-sharing, StreamReader's default
            // FileShare.Read throws/blocks on a live nuix.log. We read a snapshot of whatever bytes are
            // present at open time ("load what's currently present"); later appends simply aren't seen.
            FileStream fileStream = new FileStream(
                FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                streamReaderBufferSize, FileOptions.SequentialScan);

            using (StreamReader sr = new StreamReader(fileStream, Encoding.UTF8, false, streamReaderBufferSize))
            {
                int lineNumber = 0;
                string line = null;
                NuixLogEntry current = null;
                StringBuilder currentContent = new StringBuilder();

                BlockingCollection<string> lineQueue = new BlockingCollection<string>(500);
                BlockingCollection<NuixLogEntry> parsedQueue = new BlockingCollection<NuixLogEntry>(100);

                Task lineReaderTask = new Task(() => {
                    string readLine;
                    while ((readLine = sr.ReadLine()) != null)
                    {
                        lineQueue.Add(readLine);
                    }
                    lineQueue.Add(null);
                });

                lineReaderTask.Start();

                Task lineParserTask = new Task(() => {
                    while (true)
                    {
                        line = lineQueue.Take();
                        if (line == null) break;

                        lineNumber++;

                        // Header fast-path: only attempt a full regex match on lines that LOOK like a
                        // new-entry header for this file's format. Dated formats start with the year
                        // ("20"); the time-only engine-log format starts with "HH:" — each format
                        // advertises its own cheap predicate so we skip the costly regex on body lines.
                        if (format.LooksLikeHeaderStart(line))
                        {
                            Match parsed = format.Header.Match(line);
                            if (parsed.Success)
                            {
                                if (current != null)
                                {
                                    current.Content = SanitizeContent(currentContent.ToString().Trim());
                                    currentContent.Clear();
                                    parsedQueue.Add(current);
                                }

                                current = new NuixLogEntry();
                                current.LineNumber = lineNumber;
                                current.FilePath = FilePath;
                                current.FileName = Path.GetFileName(FilePath);
                                currentContent.AppendLine(parsed.Groups["content"].Value);

                                // The format knows how to populate timestamp/level/source/channel/elapsed
                                // from its own capture groups (Nuix tz/elapsed, Adaptive level mapping, ...).
                                format.Apply(parsed, current);

                                // Time-only formats parse only a time-of-day (on a placeholder date).
                                // Rebase onto the file's running calendar date, rolling forward a day
                                // whenever the clock goes backwards (a midnight crossing).
                                if (needDate)
                                {
                                    var tod = current.TimeStamp.TimeOfDay;
                                    if (lastTimeOfDay != TimeSpan.MinValue && tod < lastTimeOfDay)
                                    {
                                        runningDate = runningDate.AddDays(1);
                                    }
                                    lastTimeOfDay = tod;
                                    current.TimeStamp = runningDate.Add(tod);
                                }
                            }
                            else
                            {
                                // Not a new-entry header in this file's format => continuation content
                                // (strip any per-line transport prefix, e.g. the K8s timestamp).
                                currentContent.AppendLine(format.CleanContinuation(line));
                            }
                        }
                        else
                        {
                            // This line from the log should be content on a new line from
                            // a previously encountered log entry
                            currentContent.AppendLine(format.CleanContinuation(line));
                        }
                    }

                    // Make sure to kick out the final entry as well!
                    if (current != null)
                    {
                        current.Content = SanitizeContent(currentContent.ToString().Trim());
                        currentContent.Clear();
                        parsedQueue.Add(current);
                    }

                    parsedQueue.Add(null);
                });

                lineParserTask.Start();

                while (true)
                {
                    NuixLogEntry parsedEntry = parsedQueue.Take();
                    if (parsedEntry == null) { yield break; }
                    else { yield return parsedEntry; }
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
