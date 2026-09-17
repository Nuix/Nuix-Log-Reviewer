using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Turns a raw log message into a normalized "template" by masking out the variable parts
    /// (GUIDs, emails, paths, numbers, ids, ...). Grouping messages by their masked template is
    /// what powers the Patterns view: it collapses the many concrete lines into a small set of
    /// recurring shapes, so both the high-volume noise and the rare one-off lines become visible.
    ///
    /// The masking rules are applied in order (most specific first). The default rule set is built
    /// in; a power user can override or extend it via a "LogPatterns.config" file placed next to the
    /// executable. Rules are read once at startup - editing the config requires a restart (and a
    /// re-load of the logs, since templates are computed at index time). This keeps templating fast
    /// (a pre-computed field) while still allowing tuning when genuinely needed.
    ///
    /// Privacy note: masking removes variable data (emails/GUIDs/ids become placeholder tokens), so
    /// a template is inherently safer to display than a raw line - no customer values survive it.
    /// </summary>
    public sealed class LogPatternMasker
    {
        /// <summary>Max length of a produced template; longer messages are truncated.</summary>
        public const int MaxTemplateLength = 200;

        private static readonly Regex CollapseWhitespace = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly List<MaskRule> _rules;
        private readonly List<string> _groupPrefixes;

        private LogPatternMasker(List<MaskRule> rules, List<string> groupPrefixes)
        {
            _rules = rules;
            _groupPrefixes = groupPrefixes ?? new List<string>();
        }

        /// <summary>
        /// The built-in group prefixes: any message whose first line starts with one of these collapses
        /// to a single "<prefix> ..." template, bypassing token masking. Use for known-noisy message
        /// families whose internal variation is irrelevant (e.g. downstream HTTP cookie-parse warnings).
        /// </summary>
        private static List<string> DefaultGroupPrefixes()
        {
            return new List<string>
            {
                "Invalid cookie header:",
            };
        }

        /// <summary>
        /// The ordered built-in rules. Order matters: the most specific patterns (GUID, email) run
        /// before the general number rule so that, e.g., the digits inside a GUID are not masked
        /// piecemeal. Tuned against real Nuix/Automate logs.
        /// </summary>
        private static List<MaskRule> DefaultRules()
        {
            return new List<MaskRule>
            {
                new MaskRule(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<GUID>"),
                new MaskRule(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", "<EMAIL>"),
                new MaskRule(@"\b(?:[A-Za-z]:\\|\\\\|/)[^\s""']+", "<PATH>"),
                new MaskRule(@"\bhttps?://[^\s""']+", "<URL>"),
                new MaskRule(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", "<IP>"),
                new MaskRule(@"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)?\b", "<TS>"),
                // RFC-1123 / HTTP date (e.g. "Mon, 15 Jan 2024 09:30:00 GMT"), as seen in cookie
                // Expires attributes and HTTP headers.
                new MaskRule(@"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun), \d{2} \w{3} \d{4} \d{2}:\d{2}:\d{2} GMT", "<TS>"),
                // Compact log timestamp yyyyMMdd_HHmmss_fff (e.g. 20240115_093000_500), as used in
                // Automate server ping/engine lines. Without this each line's unique timestamp would
                // fragment otherwise-identical messages into distinct templates.
                new MaskRule(@"\b\d{8}_\d{6}_\d{3}\b", "<TS>"),
                // Short hex identifiers (engine/job ids). Require at least one digit so real words
                // (e.g. "deadbeef" is unlikely, but "facade"/"decade" won't match) are left alone.
                new MaskRule(@"\b(?=[0-9a-fA-F]*[0-9])[0-9a-fA-F]{6,}\b", "<ID>"),
                // Quoted dotted configuration keys (e.g. 'directory.resource.loader.cache'). Requires
                // at least one dot and only key-ish chars, so quoted plain words/sentences are left
                // intact. Collapses families like the "configuration key '...' has been deprecated in
                // favor of '...'" deprecation warnings that otherwise fragment per key.
                new MaskRule(@"'[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+'", "'<KEY>'"),
                // Number carrying a unit (0s, 12ms, 3GB, 45%): collapse metric values.
                new MaskRule(@"\b\d+(?:\.\d+)?\s?(?:ms|s|m|h|ns|us|KB|MB|GB|TB|%)\b", "#U"),
                // Any remaining bare number.
                new MaskRule(@"\b\d+(?:\.\d+)?\b", "#"),
            };
        }

        /// <summary>
        /// Builds a masker from the config file next to the executable if present and valid, otherwise
        /// from the built-in defaults. Malformed rules are skipped (never throws); if the config yields
        /// no usable rules, the defaults are used.
        /// </summary>
        public static LogPatternMasker CreateDefault()
        {
            try
            {
                string configPath = ConfigPaths.LogPatternsConfig;
                if (configPath != null && File.Exists(configPath))
                {
                    LoadConfig(configPath, out var loadedRules, out var loadedPrefixes);
                    // Only adopt the file's rules if it actually defined some; otherwise keep defaults.
                    // Group prefixes are additive/independent - use whatever the file specifies.
                    if (loadedRules.Count > 0)
                    {
                        return new LogPatternMasker(loadedRules, loadedPrefixes);
                    }
                }
            }
            catch
            {
                // Fall through to defaults on any config/IO failure.
            }

            return new LogPatternMasker(DefaultRules(), DefaultGroupPrefixes());
        }

        /// <summary>Creates a masker from an explicit rule list (used by tests/harnesses).</summary>
        public static LogPatternMasker FromRules(IEnumerable<MaskRule> rules)
        {
            return new LogPatternMasker(new List<MaskRule>(rules), new List<string>());
        }

        /// <summary>Creates a masker from explicit rules and group prefixes (used by tests/harnesses).</summary>
        public static LogPatternMasker FromRules(IEnumerable<MaskRule> rules, IEnumerable<string> groupPrefixes)
        {
            return new LogPatternMasker(new List<MaskRule>(rules), new List<string>(groupPrefixes));
        }

        /// <summary>
        /// Parses a config file into mask rules and group prefixes. Each non-blank, non-comment line is:
        ///   • a group prefix:  <c>GROUP=some literal prefix</c>  (collapses matching messages to one bucket), or
        ///   • a mask rule:     <c>regex =&gt; token</c>          (masks the matched text).
        /// Lines with an invalid regex are skipped; the rest still load.
        /// </summary>
        private static void LoadConfig(string path, out List<MaskRule> rules, out List<string> groupPrefixes)
        {
            rules = new List<MaskRule>();
            groupPrefixes = new List<string>();

            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.StartsWith("GROUP=", StringComparison.Ordinal))
                {
                    string prefix = line.Substring("GROUP=".Length).Trim();
                    if (prefix.Length > 0) groupPrefixes.Add(prefix);
                    continue;
                }

                int sep = line.IndexOf("=>", StringComparison.Ordinal);
                if (sep <= 0) continue;

                string pattern = line.Substring(0, sep).Trim();
                string token = line.Substring(sep + 2).Trim();
                if (pattern.Length == 0 || token.Length == 0) continue;

                try
                {
                    rules.Add(new MaskRule(pattern, token));
                }
                catch (ArgumentException)
                {
                    // Invalid regex - skip this rule but keep the rest.
                }
            }
        }

        /// <summary>
        /// Produces the normalized template for a message: first line only (stack traces / multi-line
        /// bodies shouldn't fragment templates), variable tokens masked, whitespace collapsed, and
        /// truncated to <see cref="MaxTemplateLength"/>. Returns "" for null/empty input.
        /// </summary>
        public string Mask(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";

            string s = message;
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) s = s.Substring(0, nl);

            // Group-prefix short-circuit: known-noisy families collapse to a single bucket regardless
            // of their internal variation. Checked on the trimmed first line, before token masking.
            string trimmed = s.TrimStart();
            foreach (var prefix in _groupPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return prefix + " \u2026"; // "<prefix> …"
                }
            }

            foreach (var rule in _rules)
            {
                s = rule.Regex.Replace(s, rule.Token);
            }

            s = CollapseWhitespace.Replace(s, " ").Trim();
            if (s.Length > MaxTemplateLength) s = s.Substring(0, MaxTemplateLength);
            return s;
        }

        /// <summary>A single ordered masking rule: a compiled regex and the token it is replaced with.</summary>
        public sealed class MaskRule
        {
            public Regex Regex { get; }
            public string Token { get; }

            public MaskRule(string pattern, string token)
            {
                Regex = new Regex(pattern, RegexOptions.Compiled);
                Token = token;
            }
        }
    }
}
