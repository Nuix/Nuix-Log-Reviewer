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

                        // Since a log entry in the format understood by this code starts with a date, and that date should start with 20xx we
                        // can use this as a quick way to determine if we even need to do the regex parse of a line.  If the line doesn't start
                        // with a 20xx value, we can assume it must be content.  If it does start with 20xx then we do the deeper analysis to determine
                        // whether this is actually a new log entry, parse the fields, etc.  This allows for skipping more costly processing in some
                        // instaces and it makes the parsing process a bit faster!
                        // Adaptive container logs also start with a timestamp beginning "20", so this
                        // coarse gate still applies to every supported format; the detected format's
                        // regex does the real match.
                        if (line.StartsWith("20"))
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
