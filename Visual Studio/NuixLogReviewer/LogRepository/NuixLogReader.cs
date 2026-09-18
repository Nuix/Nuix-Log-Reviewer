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
            // it, but Automate logs (scheduler/engine-server/engine job/init) do not.
            get { return @"^(?<timestamp>20\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)(?:\s+(?<tz>[\+\-]?\d{4}))?\s+"; }
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

        public string FilePath { get; private set; }

        public NuixLogReader(string filePath)
        {
            FilePath = filePath;
        }

        public IEnumerator<NuixLogEntry> GetEnumerator()
        {
            FileInfo fileInfo = new FileInfo(FilePath);
            if (fileInfo.Length == 0)
            {
                yield break;
            }

            CultureInfo culture = new CultureInfo("en-US");

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
                        if (line.StartsWith("20"))
                        {
                            Match parsed = LineParseRegex.Match(line);
                            if (parsed.Success)
                            {
                                if (current != null)
                                {
                                    current.Content = currentContent.ToString().Trim();
                                    currentContent.Clear();
                                    parsedQueue.Add(current);
                                }

                                current = new NuixLogEntry();

                                current.LineNumber = lineNumber;
                                current.FilePath = FilePath;
                                current.FileName = Path.GetFileName(FilePath);
                                currentContent.AppendLine(parsed.Groups["content"].Value);

                                // Timezone offset is optional (classic logs have it, Automate logs
                                // do not). Parse with the offset when present so the instant is
                                // preserved; otherwise treat the timestamp as local/unspecified.
                                string timestampText = parsed.Groups["timestamp"].Value;
                                if (parsed.Groups["tz"].Success && parsed.Groups["tz"].Value.Length > 0)
                                {
                                    current.TimeStamp = DateTime.ParseExact(
                                        timestampText + " " + parsed.Groups["tz"].Value,
                                        "yyyy-MM-dd HH:mm:ss.fff zzz", culture);
                                }
                                else
                                {
                                    current.TimeStamp = DateTime.ParseExact(
                                        timestampText, "yyyy-MM-dd HH:mm:ss.fff", culture);
                                }

                                current.Channel = parsed.Groups["channel"].Value.Trim();

                                // Elapsed is optional (absent from Automate logs).
                                if (parsed.Groups["elapsed"].Success && parsed.Groups["elapsed"].Value.Length > 0)
                                {
                                    current.Elapsed = TimeSpan.FromMilliseconds(long.Parse(parsed.Groups["elapsed"].Value, culture));
                                }
                                else
                                {
                                    current.Elapsed = TimeSpan.Zero;
                                }

                                current.Level = String.Intern(parsed.Groups["level"].Value.Trim()); // Intern since we know there is a small set of possible values
                                current.Source = parsed.Groups["source"].Value.Trim();
                            }
                            else
                            {
                                // This line from the log should be content on a new line from
                                // a previously encountered log entry
                                currentContent.AppendLine(line);
                            }
                        }
                        else
                        {
                            // This line from the log should be content on a new line from
                            // a previously encountered log entry
                            currentContent.AppendLine(line);
                        }
                    }

                    // Make sure to kick out the final entry as well!
                    if (current != null)
                    {
                        current.Content = currentContent.ToString().Trim();
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
