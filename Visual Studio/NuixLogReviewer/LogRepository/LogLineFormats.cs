using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// A recognized log-line format: a header-line regex plus how to populate a <see cref="NuixLogEntry"/>
    /// from a match. A file's format is detected once by sampling its first lines (see
    /// <see cref="LogLineFormats.Detect"/>), then applied to every line so the different producers
    /// (Nuix/Automate/Derby vs. Adaptive Security container logs) never cross-match.
    /// </summary>
    public sealed class LogLineFormat
    {
        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

        public string Name { get; }
        public Regex Header { get; }
        private readonly Action<Match, NuixLogEntry> _apply;
        private readonly Regex _continuationPrefix;

        public LogLineFormat(string name, Regex header, Action<Match, NuixLogEntry> apply, Regex continuationPrefix = null)
        {
            Name = name;
            Header = header;
            _apply = apply;
            _continuationPrefix = continuationPrefix;
        }

        /// <summary>True if the line starts a new entry in this format.</summary>
        public bool IsHeader(string line) => line != null && Header.IsMatch(line);

        /// <summary>Populates the timestamp/level/source/channel/content fields from a header match.</summary>
        public void Apply(Match m, NuixLogEntry entry) => _apply(m, entry);

        /// <summary>
        /// Normalizes a CONTINUATION line (one that isn't a new-entry header) before it's appended to the
        /// current entry's content. For container formats every physical line carries a transport prefix
        /// (the Kubernetes/CRI timestamp), including stack-trace lines - strip it so the multi-line body
        /// reads cleanly instead of being littered with per-line timestamps. Identity for plain formats.
        /// </summary>
        public string CleanContinuation(string line)
        {
            if (_continuationPrefix == null || line == null) return line;
            return _continuationPrefix.Replace(line, "");
        }

        /// <summary>
        /// Normalizes the varied level tokens seen across producers to the app's canonical set
        /// (INFO/WARN/ERROR/DEBUG/TRACE). e.g. Adaptive logs use "EROR" and ".NET" "INFORMATION".
        /// </summary>
        public static string MapLevel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            switch (raw.Trim().ToUpperInvariant())
            {
                case "EROR": case "ERR": case "ERROR": case "FATAL": case "CRITICAL": case "CRIT":
                    // FATAL/CRITICAL fold to ERROR so the level chips/tints/severity stay a small set.
                    return "ERROR";
                case "INFORMATION": case "INFO": return "INFO";
                case "WARNING": case "WARN": return "WARN";
                case "DEBUG": case "DBG": return "DEBUG";
                case "TRACE": case "VERBOSE": return "TRACE";
                default: return raw.Trim().ToUpperInvariant();
            }
        }

        /// <summary>Parses "yyyy-MM-dd HH:mm:ss.fff" (Adaptive inner timestamp); falls back to now on failure.</summary>
        internal static DateTime ParsePlainTimestamp(string ts)
        {
            if (DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss.fff", Culture, DateTimeStyles.None, out var dt))
                return dt;
            // Some lines carry more/fewer fractional digits; try a lenient parse.
            if (DateTime.TryParse(ts, Culture, DateTimeStyles.None, out dt)) return dt;
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// The set of known formats and the sampling-based detector. Formats are tried in order for
    /// detection scoring; the one matching the most of a file's first sampled lines wins.
    /// </summary>
    public static class LogLineFormats
    {
        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

        // ---- Nuix / Automate / Derby (the original composed format) ----------------------------------
        // Timestamp (millis '.' or ','; optional tz), optional [channel], optional elapsed, LEVEL,
        // source " - ", content. This mirrors the historical NuixLogReader regex.
        private static readonly Regex NuixHeader = new Regex(
            @"^(?<timestamp>20\d{2}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}[.,]\d+)(?:\s+(?<tz>[\+\-]?\d{4}))?\s+" +
            @"\[(?<channel>.+)\]\s+" +
            @"(?:(?<elapsed>\d+)\s+)?" +
            @"(?<level>(TRACE|DEBUG|INFO|WARN|ERROR))\s+" +
            @"(?<source>[^\-]+)\s-\s" +
            @"(?<content>.*$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---- Adaptive Security container logs --------------------------------------------------------
        // Every line is prefixed with the Kubernetes/CRI ingest timestamp (RFC3339, nanoseconds, 'Z').
        // Two inner shapes are seen; we key on the K8s prefix and parse the INNER timestamp (event time).
        private const string K8sPrefix = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z\s+";

        // Strips the leading Kubernetes/CRI timestamp from a continuation (non-header) line so stack
        // traces don't carry a per-line "2026-..Z " prefix in the entry's content.
        private static readonly Regex K8sContinuationPrefix = new Regex(K8sPrefix, RegexOptions.Compiled);

        // Format A ("bracket"): [innerDate innerTime LEVEL] source() | content   (.NET microservices)
        private static readonly Regex AdaptiveBracketHeader = new Regex(
            K8sPrefix +
            @"\[(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) (?<level>[A-Za-z]{3,11})\]\s*" +
            @"(?<source>[^|]*?)\s*\|\s*(?<content>.*)$",
            RegexOptions.Compiled);

        // Format B ("cpp"): innerDate innerTime LEVEL C:.. P:.. T:.. content       (C++ eps service)
        private static readonly Regex AdaptiveCppHeader = new Regex(
            K8sPrefix +
            @"(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) (?<level>[A-Za-z]{3,11}) " +
            @"C:(?<ctx>\S+) P:(?<pid>\d+) T:(?<tid>\d+) (?<content>.*)$",
            RegexOptions.Compiled);

        public static readonly LogLineFormat Nuix = new LogLineFormat("Nuix/Automate/Derby", NuixHeader, (m, e) =>
        {
            string ts = m.Groups["timestamp"].Value.Replace(',', '.'); // Derby uses comma millis
            if (m.Groups["tz"].Success && m.Groups["tz"].Value.Length > 0)
                e.TimeStamp = DateTime.ParseExact(ts + " " + m.Groups["tz"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", Culture);
            else
                e.TimeStamp = DateTime.ParseExact(ts, "yyyy-MM-dd HH:mm:ss.fff", Culture);

            e.Channel = m.Groups["channel"].Value.Trim();
            e.Elapsed = (m.Groups["elapsed"].Success && m.Groups["elapsed"].Value.Length > 0)
                ? TimeSpan.FromMilliseconds(long.Parse(m.Groups["elapsed"].Value, Culture))
                : TimeSpan.Zero;
            e.Level = string.Intern(m.Groups["level"].Value.Trim());
            e.Source = m.Groups["source"].Value.Trim();
        });

        public static readonly LogLineFormat AdaptiveBracket = new LogLineFormat("Adaptive (.NET)", AdaptiveBracketHeader, (m, e) =>
        {
            e.TimeStamp = LogLineFormat.ParsePlainTimestamp(m.Groups["timestamp"].Value);
            e.Level = string.Intern(LogLineFormat.MapLevel(m.Groups["level"].Value));
            // Source like "Nuix.Endpoint...Provider()" or empty "()"; drop trailing "()" noise.
            string src = m.Groups["source"].Value.Trim();
            if (src == "()") src = "";
            e.Source = src;
            e.Channel = "";
            e.Elapsed = TimeSpan.Zero;
        }, K8sContinuationPrefix);

        public static readonly LogLineFormat AdaptiveCpp = new LogLineFormat("Adaptive (C++)", AdaptiveCppHeader, (m, e) =>
        {
            e.TimeStamp = LogLineFormat.ParsePlainTimestamp(m.Groups["timestamp"].Value);
            e.Level = string.Intern(LogLineFormat.MapLevel(m.Groups["level"].Value));
            e.Source = "";                       // the [file:line] prefix stays in content (it's the real source hint)
            e.Channel = "T:" + m.Groups["tid"].Value; // thread id as the channel
            e.Elapsed = TimeSpan.Zero;
        }, K8sContinuationPrefix);

        /// <summary>All formats, in detection-scoring order.</summary>
        public static readonly IReadOnlyList<LogLineFormat> All = new[] { Nuix, AdaptiveBracket, AdaptiveCpp };

        /// <summary>
        /// Picks the format that matches the most of the given sample lines (first N non-blank lines of
        /// a file). Ties break by <see cref="All"/> order (Nuix first). Returns <see cref="Nuix"/> when
        /// nothing matches, so behavior is unchanged for unrecognized/near-Nuix files.
        /// </summary>
        public static LogLineFormat Detect(IEnumerable<string> sampleLines)
        {
            LogLineFormat best = Nuix;
            int bestScore = -1;
            foreach (var fmt in All)
            {
                int score = 0;
                foreach (var line in sampleLines)
                {
                    if (!string.IsNullOrEmpty(line) && fmt.IsHeader(line)) score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = fmt;
                }
            }
            return best;
        }
    }
}
