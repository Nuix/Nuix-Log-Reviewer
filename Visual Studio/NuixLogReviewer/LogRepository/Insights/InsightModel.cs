using System;
using System.Collections.Generic;
using System.Linq;

namespace NuixLogReviewer.LogRepository.Insights
{
    /// <summary>Relative importance of a finding, for sorting and color-coding in the Insights tab.</summary>
    public enum InsightSeverity
    {
        Info = 0,
        Notice = 1,
        Warning = 2,
        Critical = 3,
    }

    /// <summary>
    /// One "thread to begin pulling on": something notable about the loaded corpus, with a ready-made
    /// query (in the app's existing query language) that hones the view onto the evidence, and an
    /// optional position (entry id or event time) to scroll to. Deliberately a plain, self-contained
    /// record so both compiled and (later) scripted detectors emit the same thing, and so a finding can
    /// seed a ViewState / bookmark unchanged.
    /// </summary>
    public sealed class Insight
    {
        public InsightSeverity Severity { get; set; } = InsightSeverity.Notice;
        /// <summary>Short headline, e.g. "Error spike at 18:34".</summary>
        public string Title { get; set; }
        /// <summary>One-line supporting detail, e.g. "3,142 errors in ~2 min (18% of all errors)".</summary>
        public string Detail { get; set; }
        /// <summary>Which detector produced this (for grouping + a source label). Set by the engine.</summary>
        public string Source { get; set; }
        /// <summary>Query (existing syntax) that isolates the evidence. May be blank for informational rows.</summary>
        public string Query { get; set; }
        /// <summary>Optional entry id to select/scroll to after running the query.</summary>
        public long? PositionEntryId { get; set; }
        /// <summary>Optional event time (ticks) to scroll to (when no specific entry id is known).</summary>
        public long? PositionTicks { get; set; }

        /// <summary>
        /// Whether <see cref="Query"/> parsed cleanly. The engine sets this (via the index's validator)
        /// for detector-supplied queries; when false the UI shows the finding but disables the jump and
        /// surfaces <see cref="QueryError"/>. Built-in detectors' queries are trusted/compiler-built.
        /// </summary>
        public bool QueryValid { get; set; } = true;
        public string QueryError { get; set; }

        public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    }

    /// <summary>
    /// A frozen, read-only snapshot of the WHOLE loaded corpus, built once after load from the existing
    /// summary passes. Detectors analyze this (never the raw index), so they're cheap, deterministic and
    /// safe to expose to user scripts later. All figures are for the full set (unfiltered by the view's
    /// hidden classifiers/files/patterns): Insights describe "what's in these logs", not the muted view.
    /// </summary>
    public sealed class InsightContext
    {
        public int Total { get; }
        public int Info { get; }
        public int Warn { get; }
        public int Error { get; }
        public int Debug { get; }
        public long? MinTicks { get; }
        public long? MaxTicks { get; }

        /// <summary>Per-classifier-flag counts across the whole set.</summary>
        public IReadOnlyList<FlagStat> Flags { get; }
        /// <summary>Per-source-file counts across the whole set (full path + short display name).</summary>
        public IReadOnlyList<FileStat> Files { get; }
        /// <summary>Worker jobs with lifespan + warn/error counts.</summary>
        public IReadOnlyList<LogSearchIndex.JobInfo> Jobs { get; }
        /// <summary>Message-template patterns (Drain-folded), with per-level counts and members.</summary>
        public IReadOnlyList<LogSearchIndex.LogPattern> Patterns { get; }
        /// <summary>Coarse time-series (per-bucket INFO/WARN/ERROR) over the whole window.</summary>
        public LogSearchIndex.TimeSeries Timeline { get; }

        public InsightContext(
            LogSearchIndex.FilteredSetSummary summary,
            IReadOnlyDictionary<string, string> fileDisplayNames,
            IEnumerable<LogSearchIndex.JobInfo> jobs,
            IEnumerable<LogSearchIndex.LogPattern> patterns,
            LogSearchIndex.TimeSeries timeline)
        {
            summary = summary ?? new LogSearchIndex.FilteredSetSummary();
            Total = summary.Total; Info = summary.Info; Warn = summary.Warn; Error = summary.Error; Debug = summary.Debug;
            MinTicks = summary.MinTicks; MaxTicks = summary.MaxTicks;

            Flags = summary.FlagCounts
                .Where(kv => kv.Value > 0)
                .Select(kv => new FlagStat { Name = kv.Key, Count = kv.Value })
                .OrderByDescending(f => f.Count).ToList();

            Files = summary.FileCounts
                .Where(kv => kv.Value > 0)
                .Select(kv => new FileStat
                {
                    Path = kv.Key,
                    DisplayName = (fileDisplayNames != null && fileDisplayNames.TryGetValue(kv.Key, out var dn)) ? dn : kv.Key,
                    Count = kv.Value,
                })
                .OrderByDescending(f => f.Count).ToList();

            Jobs = (jobs ?? Enumerable.Empty<LogSearchIndex.JobInfo>()).ToList();
            Patterns = (patterns ?? Enumerable.Empty<LogSearchIndex.LogPattern>()).ToList();
            Timeline = timeline;
        }

        /// <summary>
        /// Peak GC "percentage time" observed across the run (0..100), or null if no GC-monitor lines
        /// were found. Surfaced by an extra pass in BuildInsights because the actual value is masked out
        /// of the pattern templates. Drives the GC-pressure detector.
        /// </summary>
        public double? GcPeakPercent { get; set; }

        /// <summary>Max cumulative GC "total time" (seconds) observed, or null if unknown.</summary>
        public double? GcMaxTotalSeconds { get; set; }

        public sealed class FlagStat { public string Name; public int Count; }
        public sealed class FileStat { public string Path; public string DisplayName; public int Count; }
    }

    /// <summary>A source of insights. Implementations analyze the frozen context and return 0+ findings.</summary>
    public interface IInsightDetector
    {
        /// <summary>Human-readable detector name, used as the grouping label on findings.</summary>
        string Name { get; }

        /// <summary>Whether detector-supplied queries should be validated by the engine (true for
        /// untrusted/scripted sources; false for compiled built-ins whose queries are known-good).</summary>
        bool ValidateQueries { get; }

        IEnumerable<Insight> Analyze(InsightContext ctx);
    }

    /// <summary>
    /// Runs a set of detectors over one context and returns their findings, each tagged with its source
    /// detector, grouped by source then ordered by severity (desc). Optionally validates detector queries
    /// via a caller-supplied predicate (the index's parser) so a bad query disables the jump rather than
    /// throwing when clicked. Never throws for one misbehaving detector - it's isolated and skipped.
    /// </summary>
    public sealed class InsightEngine
    {
        private readonly IReadOnlyList<IInsightDetector> _detectors;

        public InsightEngine(IEnumerable<IInsightDetector> detectors)
        {
            _detectors = (detectors ?? Enumerable.Empty<IInsightDetector>()).ToList();
        }

        /// <param name="queryValidator">
        /// Returns (isValid, errorMessage) for a query string; used only for detectors that opt into
        /// validation. Null => skip validation entirely.
        /// </param>
        public IList<Insight> Run(InsightContext ctx, Func<string, (bool ok, string error)> queryValidator = null)
        {
            var all = new List<Insight>();
            if (ctx == null) return all;

            foreach (var detector in _detectors)
            {
                IEnumerable<Insight> found;
                try
                {
                    found = detector.Analyze(ctx) ?? Enumerable.Empty<Insight>();
                }
                catch
                {
                    // Fail-soft: a broken detector must not sink the whole panel.
                    continue;
                }

                foreach (var insight in found)
                {
                    if (insight == null) continue;
                    insight.Source = detector.Name;

                    if (detector.ValidateQueries && queryValidator != null && insight.HasQuery)
                    {
                        var (ok, error) = queryValidator(insight.Query);
                        insight.QueryValid = ok;
                        insight.QueryError = ok ? null : error;
                    }

                    all.Add(insight);
                }
            }

            // Group by source, then most-severe first within each group.
            return all
                .OrderBy(i => i.Source, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(i => i.Severity)
                .ToList();
        }
    }
}
