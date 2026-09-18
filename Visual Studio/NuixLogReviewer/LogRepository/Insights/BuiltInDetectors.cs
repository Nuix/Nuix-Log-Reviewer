using System;
using System.Collections.Generic;
using System.Linq;

namespace NuixLogReviewer.LogRepository.Insights
{
    /// <summary>
    /// Tunable thresholds for the built-in detectors. Plain fields so this can later be loaded from a
    /// Configuration/ file (like the other tunables) without changing detector code.
    /// </summary>
    public sealed class InsightThresholds
    {
        /// <summary>A file is "error-dominant" when it holds at least this share of ALL errors.</summary>
        public double FileErrorShare = 0.40;
        /// <summary>Minimum errors before the error-dominant-file rule fires (avoid tiny sets).</summary>
        public int FileErrorMin = 20;
        /// <summary>A time bucket is a "spike" when its error count exceeds mean + this many std-devs.</summary>
        public double SpikeSigma = 3.0;
        /// <summary>Minimum errors in a bucket before it can be called a spike.</summary>
        public int SpikeMinErrors = 25;
        /// <summary>A job is error-heavy when errors/entries >= this AND it has >= JobErrorMin errors.</summary>
        public double JobErrorRate = 0.20;
        public int JobErrorMin = 10;
        /// <summary>Rare-pattern ceiling: ERROR templates occurring at most this many times are surfaced.</summary>
        public int RarePatternMaxCount = 3;
        /// <summary>Max rare-error patterns to surface (avoid flooding).</summary>
        public int RarePatternLimit = 5;
        /// <summary>A single template "dominates" when it is at least this share of all lines.</summary>
        public double DominantPatternShare = 0.50;

        /// <summary>A silence gap is notable when its empty span is at least this many minutes.</summary>
        public double SilenceGapMinutes = 10.0;
        /// <summary>
        /// AND the gap must be at least this multiple of the log's TYPICAL gap between active periods,
        /// so naturally sparse/periodic logs (e.g. a GC line every ~15 min) don't flag routine spacing
        /// as a "quiet period". Only a gap that's a clear outlier vs the log's own cadence fires.
        /// </summary>
        public double SilenceGapOutlierFactor = 4.0;
        /// <summary>Error ramp-up: last-third mean must exceed first-third mean by at least this factor.</summary>
        public double RampUpFactor = 3.0;
        /// <summary>Minimum errors in the window before ramp-up/onset detectors bother.</summary>
        public int OnsetMinErrors = 30;
    }

    /// <summary>Error/warn spikes in the time-series: "something went wrong HERE".</summary>
    public sealed class ErrorSpikeDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public ErrorSpikeDetector(InsightThresholds t) { _t = t; }
        public string Name => "Error spikes";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            var ts = ctx.Timeline;
            if (ts?.Counts == null || ts.Buckets <= 2 || !ts.MinTicks.HasValue || !ts.MaxTicks.HasValue)
                yield break;

            int n = ts.Buckets;
            var errs = new double[n];
            for (int b = 0; b < n; b++) errs[b] = ts.Counts[b, 2]; // level 2 = ERROR

            double mean = errs.Average();
            double variance = errs.Select(e => (e - mean) * (e - mean)).Average();
            double sd = Math.Sqrt(variance);
            if (sd <= 0) yield break;

            double bucketTicks = (double)(ts.MaxTicks.Value - ts.MinTicks.Value) / n;

            for (int b = 0; b < n; b++)
            {
                if (errs[b] >= _t.SpikeMinErrors && errs[b] > mean + _t.SpikeSigma * sd)
                {
                    long startTicks = ts.MinTicks.Value + (long)(b * bucketTicks);
                    long endTicks = ts.MinTicks.Value + (long)((b + 1) * bucketTicks);
                    var start = new DateTime(startTicks);
                    double share = ctx.Error > 0 ? errs[b] / ctx.Error : 0;

                    yield return new Insight
                    {
                        Severity = InsightSeverity.Warning,
                        Title = $"Error spike around {start:yyyy-MM-dd HH:mm}",
                        Detail = $"{errs[b]:N0} errors in one time slice ({share:P0} of all errors)",
                        // Errors within the spike's time window.
                        Query = $"level:error AND timestamp:[{startTicks} TO {endTicks}]",
                        PositionTicks = startTicks,
                    };
                }
            }
        }
    }

    /// <summary>A source file that holds a disproportionate share of the errors.</summary>
    public sealed class ErrorDominantFileDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public ErrorDominantFileDetector(InsightThresholds t) { _t = t; }
        public string Name => "Error-heavy files";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            if (ctx.Error < _t.FileErrorMin) yield break;

            // FileCounts are TOTAL entries per file, not errors, so approximate error-dominance by
            // querying each top file's error share would need a pass; instead surface files whose TOTAL
            // volume dominates AND the corpus is error-heavy, plus give the exact error query to pull on.
            // (A precise per-file error count is a natural future refinement.)
            foreach (var f in ctx.Files.Take(5))
            {
                double share = ctx.Total > 0 ? (double)f.Count / ctx.Total : 0;
                if (share >= _t.FileErrorShare)
                {
                    yield return new Insight
                    {
                        Severity = InsightSeverity.Notice,
                        Title = $"{f.DisplayName} dominates the log",
                        Detail = $"{f.Count:N0} entries ({share:P0} of all lines)",
                        Query = FileDisplay.DrillDownQuery(f.Path) + " AND level:error",
                    };
                }
            }
        }
    }

    /// <summary>Worker jobs that are error-heavy relative to their size.</summary>
    public sealed class JobFailureDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public JobFailureDetector(InsightThresholds t) { _t = t; }
        public string Name => "Troubled jobs";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            foreach (var j in ctx.Jobs)
            {
                if (j.ErrorCount < _t.JobErrorMin) continue;
                double rate = j.EntryCount > 0 ? (double)j.ErrorCount / j.EntryCount : 0;
                if (rate >= _t.JobErrorRate)
                {
                    yield return new Insight
                    {
                        Severity = InsightSeverity.Warning,
                        Title = $"Job {j.JobId} is error-heavy",
                        Detail = $"{j.ErrorCount:N0} errors of {j.EntryCount:N0} entries ({rate:P0}), lasted {j.DurationText}",
                        Query = $"job:{j.JobId} AND level:error",
                    };
                }
            }
        }
    }

    /// <summary>Rare ERROR templates (often the most interesting one-offs) and any single dominating template.</summary>
    public sealed class PatternAnomalyDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public PatternAnomalyDetector(InsightThresholds t) { _t = t; }
        public string Name => "Notable patterns";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            // A single template that dominates the whole corpus (noise, or a retry storm).
            foreach (var p in ctx.Patterns)
            {
                double share = ctx.Total > 0 ? (double)p.Count / ctx.Total : 0;
                if (share >= _t.DominantPatternShare)
                {
                    yield return new Insight
                    {
                        Severity = InsightSeverity.Notice,
                        Title = "One message dominates the log",
                        Detail = $"{p.Count:N0} lines ({share:P0}) match a single template: \"{Trim(p.Template)}\"",
                        Query = PatternQuery.DrillDownQuery(p.MemberTemplates ?? new[] { p.Template }),
                    };
                }
            }

            // Rare ERROR-leaning templates: few occurrences, but errors - the interesting long tail.
            int emitted = 0;
            foreach (var p in ctx.Patterns
                .Where(p => p.Error > 0 && p.Count <= _t.RarePatternMaxCount)
                .OrderBy(p => p.Count))
            {
                if (emitted++ >= _t.RarePatternLimit) break;
                yield return new Insight
                {
                    Severity = InsightSeverity.Notice,
                    Title = "Rare error message",
                    Detail = $"Only {p.Count:N0}x: \"{Trim(p.Template)}\"",
                    Query = PatternQuery.DrillDownQuery(p.MemberTemplates ?? new[] { p.Template }),
                    PositionEntryId = p.Ids != null && p.Ids.Count > 0 ? p.Ids[0] : (long?)null,
                };
            }
        }

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 80 ? s.Substring(0, 80) + "…" : s);
    }

    /// <summary>Longest stretch with no logged activity at all - often a hang, stall, or GC pause.</summary>
    public sealed class SilenceGapDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public SilenceGapDetector(InsightThresholds t) { _t = t; }
        public string Name => "Silence gaps";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            var ts = ctx.Timeline;
            if (ts?.Counts == null || ts.Buckets <= 2 || !ts.MinTicks.HasValue || !ts.MaxTicks.HasValue)
                yield break;

            int n = ts.Buckets;
            double bucketTicks = (double)(ts.MaxTicks.Value - ts.MinTicks.Value) / n;
            double bucketMinutes = bucketTicks / TimeSpan.TicksPerMinute;
            if (bucketMinutes <= 0) yield break;

            // Find the longest run of fully-empty buckets (no INFO/WARN/ERROR), and collect ALL empty-run
            // lengths BETWEEN active buckets so we can judge the longest against the log's typical cadence.
            int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
            var interRunLengths = new List<int>();
            bool seenActive = false;
            for (int b = 0; b < n; b++)
            {
                bool empty = ts.Counts[b, 0] == 0 && ts.Counts[b, 1] == 0 && ts.Counts[b, 2] == 0;
                if (empty)
                {
                    if (curStart < 0) { curStart = b; curLen = 0; }
                    curLen++;
                    if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
                }
                else
                {
                    // An empty run that sits BETWEEN two active buckets is a real inter-activity gap
                    // (leading empties before the first activity aren't). Record it for the median.
                    if (curLen > 0 && seenActive) { interRunLengths.Add(curLen); }
                    curStart = -1; curLen = 0;
                    seenActive = true;
                }
            }

            if (bestStart < 0) yield break;
            double gapMinutes = bestLen * bucketMinutes;
            if (gapMinutes < _t.SilenceGapMinutes) yield break;

            // Outlier gate: the gap must dwarf the log's TYPICAL inter-activity spacing, so periodic
            // sparse logs (a heartbeat/GC line every N minutes) don't flag their normal cadence.
            if (interRunLengths.Count > 0)
            {
                interRunLengths.Sort();
                double medianRun = interRunLengths[interRunLengths.Count / 2];
                if (medianRun > 0 && bestLen < _t.SilenceGapOutlierFactor * medianRun)
                {
                    yield break; // longest gap is not a clear outlier vs the log's own rhythm
                }
            }

            long gapStartTicks = ts.MinTicks.Value + (long)(bestStart * bucketTicks);
            long gapEndTicks = ts.MinTicks.Value + (long)((bestStart + bestLen) * bucketTicks);
            var gapStart = new DateTime(gapStartTicks);
            var gapEnd = new DateTime(gapEndTicks);

            yield return new Insight
            {
                Severity = InsightSeverity.Notice,
                // Show the FULL span so it's clear the gap itself is empty; the jump lands at the end
                // (where activity resumes), since the gap window itself has no rows to show.
                Title = $"Quiet period (~{gapMinutes:N0} min): {gapStart:HH:mm} \u2192 {gapEnd:HH:mm}",
                Detail = $"No log activity from {gapStart:yyyy-MM-dd HH:mm} to {gapEnd:HH:mm} (possible hang/stall/idle). "
                       + "Opens the entries where logging resumes. Times are approximate (chart-bucket granularity).",
                // Land on the events resuming just after the gap.
                Query = $"timestamp:[{gapEndTicks} TO {ts.MaxTicks.Value}]",
                PositionTicks = gapEndTicks,
            };
        }
    }

    /// <summary>Error rate climbing toward the end of the window (a degrading run).</summary>
    public sealed class ErrorRampUpDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public ErrorRampUpDetector(InsightThresholds t) { _t = t; }
        public string Name => "Error trend";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            var ts = ctx.Timeline;
            if (ts?.Counts == null || ts.Buckets < 6 || ctx.Error < _t.OnsetMinErrors
                || !ts.MinTicks.HasValue || !ts.MaxTicks.HasValue)
                yield break;

            int n = ts.Buckets;
            int third = n / 3;
            if (third < 1) yield break;

            double firstMean = 0, lastMean = 0;
            for (int b = 0; b < third; b++) firstMean += ts.Counts[b, 2];
            for (int b = n - third; b < n; b++) lastMean += ts.Counts[b, 2];
            firstMean /= third;
            lastMean /= third;

            // Ramp-up: meaningful late error rate that's a strong multiple of the early rate.
            if (lastMean >= 1 && lastMean >= _t.RampUpFactor * Math.Max(firstMean, 0.5))
            {
                long lastThirdStart = ts.MinTicks.Value + (long)((long)(n - third) * (ts.MaxTicks.Value - ts.MinTicks.Value) / n);
                yield return new Insight
                {
                    Severity = InsightSeverity.Warning,
                    Title = "Errors ramping up over time",
                    Detail = $"Error rate rose from ~{firstMean:N1} to ~{lastMean:N1} per time slice toward the end.",
                    Query = $"level:error AND timestamp:[{lastThirdStart} TO {ts.MaxTicks.Value}]",
                    PositionTicks = lastThirdStart,
                };
            }
        }
    }

    /// <summary>When errors first appear (approx.), as an anchor for "things were fine until here".</summary>
    public sealed class ErrorOnsetDetector : IInsightDetector
    {
        private readonly InsightThresholds _t;
        public ErrorOnsetDetector(InsightThresholds t) { _t = t; }
        public string Name => "First errors";
        public bool ValidateQueries => false;

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            if (ctx.Error < _t.OnsetMinErrors || !ctx.MinTicks.HasValue) yield break;

            // Earliest first-seen among error-bearing patterns approximates when errors began.
            DateTime? onset = null;
            foreach (var p in ctx.Patterns)
            {
                if (p.Error > 0 && (onset == null || p.FirstSeen < onset.Value)) { onset = p.FirstSeen; }
            }
            if (onset == null) yield break;

            // Only interesting if errors DIDN'T start right at the beginning (i.e. there was a clean run).
            long cleanRunTicks = onset.Value.Ticks - ctx.MinTicks.Value;
            if (cleanRunTicks < TimeSpan.TicksPerMinute) yield break; // errors basically from the start

            var cleanRun = TimeSpan.FromTicks(cleanRunTicks);
            string span = cleanRun.TotalHours >= 1 ? $"{cleanRun.TotalHours:N1} h" : $"{cleanRun.TotalMinutes:N0} min";

            yield return new Insight
            {
                Severity = InsightSeverity.Notice,
                Title = $"Errors first appear at {onset.Value:yyyy-MM-dd HH:mm}",
                Detail = $"The log ran ~{span} before its first error - worth checking what changed there.",
                Query = $"level:error AND timestamp:[{onset.Value.Ticks} TO {(ctx.MaxTicks ?? onset.Value.Ticks)}]",
                PositionTicks = onset.Value.Ticks,
            };
        }
    }
}
