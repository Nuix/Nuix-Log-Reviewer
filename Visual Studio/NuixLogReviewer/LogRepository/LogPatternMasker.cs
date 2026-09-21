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
        // Exception-aware templating: append the first Java throwable class to the template so
        // same-preamble/different-exception errors don't collapse. Default on; disabled via config
        // ("EXCEPTIONS=off"). See Mask / FirstExceptionClass.
        private readonly bool _exceptionAware;

        private LogPatternMasker(List<MaskRule> rules, List<string> groupPrefixes, bool exceptionAware = true)
        {
            _rules = rules;
            _groupPrefixes = groupPrefixes ?? new List<string>();
            _exceptionAware = exceptionAware;
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
                    LoadConfig(configPath, out var loadedRules, out var loadedPrefixes, out var exceptionAware);
                    // Only adopt the file's rules if it actually defined some; otherwise keep defaults.
                    // Group prefixes are additive/independent - use whatever the file specifies.
                    if (loadedRules.Count > 0)
                    {
                        return new LogPatternMasker(loadedRules, loadedPrefixes, exceptionAware);
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

        /// <summary>Creates a masker from explicit rules, group prefixes, and the exception-aware toggle.</summary>
        public static LogPatternMasker FromRules(IEnumerable<MaskRule> rules, IEnumerable<string> groupPrefixes, bool exceptionAware)
        {
            return new LogPatternMasker(new List<MaskRule>(rules), new List<string>(groupPrefixes), exceptionAware);
        }

        /// <summary>
        /// Parses a config file into mask rules and group prefixes. Each non-blank, non-comment line is:
        ///   • a group prefix:  <c>GROUP=some literal prefix</c>  (collapses matching messages to one bucket), or
        ///   • a mask rule:     <c>regex =&gt; token</c>          (masks the matched text).
        /// Lines with an invalid regex are skipped; the rest still load.
        /// </summary>
        private static void LoadConfig(string path, out List<MaskRule> rules, out List<string> groupPrefixes, out bool exceptionAware)
        {
            rules = new List<MaskRule>();
            groupPrefixes = new List<string>();
            exceptionAware = true; // default on

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

                // EXCEPTIONS=on|off toggles exception-aware templating (append first throwable class).
                if (line.StartsWith("EXCEPTIONS=", StringComparison.OrdinalIgnoreCase))
                {
                    string val = line.Substring("EXCEPTIONS=".Length).Trim();
                    exceptionAware = !val.Equals("off", StringComparison.OrdinalIgnoreCase)
                                  && !val.Equals("false", StringComparison.OrdinalIgnoreCase)
                                  && !val.Equals("0", StringComparison.Ordinal);
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
        /// truncated to <see cref="MaxTemplateLength"/>. When exception-aware templating is enabled
        /// (the default) and the message contains a stack trace, the first throwable's class is appended
        /// as " | ex:&lt;class&gt;" so same-preamble/different-exception errors don't collapse. Returns
        /// "" for null/empty input.
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

            // Exception-aware templating: many multi-line errors share an identical masked FIRST LINE
            // (an object-graph / "[parent=...]" preamble) but represent different failures, distinguished
            // only by an exception on a LATER line - e.g. a "java.lang.NullPointerException" vs a
            // "com.nuix...slack...exception" thrown from the same call site. Masking only the first line
            // (and capping at MaxTemplateLength) collapses these together. To split them by the thing
            // that actually differs, we detect the FIRST Java throwable in the message and append its
            // class as a compact tag. Appended AFTER the length cap so the discriminator always survives
            // truncation of a long preamble. Only affects messages that contain a recognizable exception;
            // everything else masks exactly as before (no fragmentation of well-grouped families).
            if (_exceptionAware)
            {
                var ex = FirstException(message);
                if (ex != null)
                {
                    s = s + " | ex:" + ex.Value.Class;
                    // Include the normalized message so unrelated failures of the SAME class (e.g. two
                    // different NullPointerExceptions) split by their message shape. Quoted signatures/
                    // values in the message are masked to <Q> so same-shape messages still collapse.
                    string normMsg = NormalizeExceptionMessage(ex.Value.Message);
                    if (normMsg.Length > 0)
                    {
                        s = s + ": " + normMsg;
                    }
                }
            }

            return s;
        }

        // A line that is a fully-qualified class name optionally followed by ": message" - the Java
        // Throwable.toString() shape. Group 1 is the class, group 2 (optional) is the message text.
        // Anchored at line start (after optional leading whitespace) to avoid matching FQCNs that appear
        // mid-sentence. Deliberately does NOT require an "Exception/Error" suffix, so obfuscated
        // throwables (e.g. "com.nuix...exception.a") are caught too - the following "\tat " stack line
        // is what confirms it's a real throwable.
        private static readonly Regex ExceptionHeadLine = new Regex(
            @"^\s*(?:Caused by:\s*)?([a-zA-Z_$][\w$]*(?:\.[a-zA-Z_$][\w$]*)+)(?::\s*(.*))?\s*$",
            RegexOptions.Compiled);

        // A stack frame line ("    at com.foo.Bar.baz(...)"), used to confirm the preceding candidate
        // line is actually a throwable header rather than a coincidental dotted token.
        private static readonly Regex StackFrameLine = new Regex(@"^\s*at\s+\S", RegexOptions.Compiled);

        // Double- or single-quoted content inside an exception MESSAGE (e.g. the quoted method/type
        // signatures in an NPE's "Cannot invoke "X.b(String)" ..." text). Masked to <Q> so messages of
        // the same shape collapse while different shapes stay distinct. Applied to the message only.
        private static readonly Regex QuotedContent = new Regex("\"[^\"]*\"|'[^']*'", RegexOptions.Compiled);

        /// <summary>Max length of the appended (normalized) exception message.</summary>
        public const int MaxExceptionMessageLength = 100;

        /// <summary>The class + (raw) message of the first throwable found in a log message.</summary>
        internal readonly struct ExceptionInfo
        {
            public readonly string Class;
            public readonly string Message; // raw (un-normalized); may be null/empty
            public ExceptionInfo(string cls, string msg) { Class = cls; Message = msg; }
        }

        /// <summary>
        /// Finds the FIRST Java throwable in the message: a line of the form
        /// <c>fully.qualified.ClassName: message</c> (or just the class) that is immediately followed by
        /// a stack-frame line (<c>at ...</c>). Returns the class and its (raw) message text, or null if
        /// the message has no recognizable exception. The stack-frame confirmation keeps ordinary dotted
        /// tokens (config keys, package references in prose) from being mistaken for exceptions.
        /// </summary>
        internal static ExceptionInfo? FirstException(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            if (message.IndexOf("at ", StringComparison.Ordinal) < 0) return null; // fast reject: no stack

            var lines = message.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                var m = ExceptionHeadLine.Match(line);
                if (!m.Success) continue;

                // Confirm with the next non-blank line being a stack frame ("at ...").
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string next = lines[j].TrimEnd('\r');
                    if (next.Trim().Length == 0) continue;
                    if (StackFrameLine.IsMatch(next))
                    {
                        string cls = m.Groups[1].Value;
                        string msg = m.Groups[2].Success ? m.Groups[2].Value : null;
                        return new ExceptionInfo(cls, msg);
                    }
                    break; // next meaningful line isn't a stack frame -> not a throwable header
                }
            }
            return null;
        }

        /// <summary>Convenience: the class of the first throwable, or null. (Message-agnostic.)</summary>
        internal static string FirstExceptionClass(string message) => FirstException(message)?.Class;

        /// <summary>
        /// Normalizes an exception MESSAGE for templating: masks quoted content (method/type signatures,
        /// values) to &lt;Q&gt;, applies the standard token mask rules (GUID/number/path/id/...), collapses
        /// whitespace, and caps length. Same-shape messages collapse; different shapes stay distinct.
        /// </summary>
        private string NormalizeExceptionMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "";
            string s = QuotedContent.Replace(msg, "<Q>");
            foreach (var rule in _rules)
            {
                s = rule.Regex.Replace(s, rule.Token);
            }
            s = CollapseWhitespace.Replace(s, " ").Trim();
            if (s.Length > MaxExceptionMessageLength) s = s.Substring(0, MaxExceptionMessageLength);
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
