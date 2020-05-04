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
            get { return @"(?<timestamp>20[0-9]{2}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+\s+[\+\-]?[0-9]{4})\s+"; }
        }

        public static string ChannelExpression
        {
            get { return @"\[(?<channel>[^\\]+)\]\s"; }
        }

        public static string ElapsedExpression
        {
            get { return @"(?<elapsed>[0-9]+)\s"; }
        }

        public static string LevelExpression
        {
            get { return @"(?<level>[A-Z]+)\s+"; }
        }

        public static string SourceExpression
        {
            get { return @"(?<source>[^\s]+)\s-\s"; }
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

            CultureInfo cultures = new CultureInfo("en-US");

            //int fileStreamBufferSize = 1024 * 4;
            int streamReaderBufferSize = 1024 * 1024 * 32;

            //using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, fileStreamBufferSize, FileOptions.SequentialScan))
            //using (BufferedStream bs = new BufferedStream(fs, bufferSize))
            using (StreamReader sr = new StreamReader(FilePath, Encoding.UTF8, false, streamReaderBufferSize))
            {
                int lineNumber = 0;
                string line = null;
                NuixLogEntry current = null;
                StringBuilder currentContent = new StringBuilder();

                while (!sr.EndOfStream)
                {
                    line = sr.ReadLine();
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
                                current.Content = currentContent.ToString();
                                currentContent.Clear();
                                yield return current;
                            }

                            current = new NuixLogEntry();

                            DateTime parsedTimeStamp = DateTime.MinValue;
                            TimeSpan parsedElapsed = TimeSpan.Zero;

                            current.LineNumber = lineNumber;
                            current.FilePath = FilePath;
                            current.FileName = Path.GetFileName(FilePath);
                            currentContent.AppendLine(parsed.Groups["content"].Value);
                            if (DateTime.TryParse(parsed.Groups["timestamp"].Value, out parsedTimeStamp))
                            {
                                current.TimeStamp = DateTime.Parse(parsed.Groups["timestamp"].Value, cultures);
                            }
                            else
                            {
                                current.TimeStamp = DateTime.MinValue;
                            }

                            current.Channel = parsed.Groups["channel"].Value.Trim();
                            current.Elapsed = TimeSpan.FromMilliseconds(long.Parse(parsed.Groups["elapsed"].Value, cultures));
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

                if (current != null)
                {
                    current.Content = currentContent.ToString().Trim();
                    currentContent.Clear();
                    yield return current;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
