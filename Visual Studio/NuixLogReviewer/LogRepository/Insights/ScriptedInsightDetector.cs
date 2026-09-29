using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using NuixLogReviewer.LogRepository.Classifiers.Scripting.Diagnostics;

namespace NuixLogReviewer.LogRepository.Insights
{
    /// <summary>
    /// An insight detector backed by a user-supplied, sandboxed JavaScript file. The script evaluates to
    /// an object exposing <c>analyze(ctx)</c>, which receives a read-only snapshot of the whole loaded
    /// corpus (the same data the built-in detectors see) and returns 0+ finding objects.
    ///
    /// Unlike scriptable classifiers (which run per-line, millions of times), a detector runs ONCE over
    /// pre-computed summaries, so there's no hot path: the whole context is materialized into plain JS
    /// values up front and <c>analyze</c> is invoked a single time. Sandbox posture matches the
    /// classifiers: no CLR/BCL/IO/network, bounded by statement/recursion/time limits, fail-soft (a
    /// script that throws, times out, or returns junk yields no findings and never breaks the panel).
    ///
    /// Finding shape returned by analyze():
    ///   { severity?: "info"|"notice"|"warning", title: string, detail?: string,
    ///     query?: string, positionEntryId?: number, positionTicks?: number }
    /// Each ctx.files/jobs/patterns/flags item carries a precomputed, correctly-escaped <c>query</c>
    /// string (patterns' is member-expanded), so a script just copies the relevant item's query into
    /// its finding rather than building query syntax itself.
    /// </summary>
    public sealed class ScriptedInsightDetector : IInsightDetector
    {
        private const int MaxStatements = 2_000_000; // whole-corpus analysis may legitimately loop a lot
        private const int MaxRecursionDepth = 128;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly string _sourcePath;
        private readonly string _source;

        public string Name { get; }
        // Script-produced queries are untrusted; the engine validates them so a bad one disables its jump.
        public bool ValidateQueries => true;

        public ScriptedInsightDetector(string sourcePath, string source, string name)
        {
            _sourcePath = sourcePath;
            _source = source;
            Name = string.IsNullOrWhiteSpace(name)
                ? System.IO.Path.GetFileNameWithoutExtension(sourcePath ?? "insight script")
                : name;
        }

        public IEnumerable<Insight> Analyze(InsightContext ctx)
        {
            Engine engine;
            JsValue analyze;
            try
            {
                engine = CreateSandboxedEngine();
                var module = engine.Evaluate(_source);
                analyze = module.AsObject().Get("analyze");
                if (analyze == null || !analyze.IsObject() || !(analyze is Jint.Native.Function.Function))
                {
                    ScriptDiagnostics.ReportOnce(_sourcePath, "script has no analyze(ctx) function; skipped.");
                    return Array.Empty<Insight>();
                }
            }
            catch (Exception ex)
            {
                ScriptDiagnostics.ReportOnce(_sourcePath, "could not load insight script: " + ex.Message);
                return Array.Empty<Insight>();
            }

            try
            {
                JsValue ctxView = BuildContextView(engine, ctx);
                JsValue result = engine.Invoke(analyze, ctxView);
                return MaterializeFindings(result);
            }
            catch (TimeoutException)
            {
                ScriptDiagnostics.ReportOnce(_sourcePath, "analyze() exceeded the time limit; skipped.");
                return Array.Empty<Insight>();
            }
            catch (JavaScriptException jsEx)
            {
                ScriptDiagnostics.ReportOnce(_sourcePath, "analyze() threw: " + jsEx.Message);
                return Array.Empty<Insight>();
            }
            catch (Exception ex)
            {
                ScriptDiagnostics.ReportOnce(_sourcePath, "analyze() failed: " + ex.Message);
                return Array.Empty<Insight>();
            }
        }

        internal static Engine CreateSandboxedEngine()
        {
            return new Engine(options =>
            {
                options.Strict();
                options.MaxStatements(MaxStatements);
                options.LimitRecursion(MaxRecursionDepth);
                options.TimeoutInterval(Timeout);
                // No AllowClr: CLR/BCL interop stays off.
            });
        }

        /// <summary>
        /// Builds the read-only <c>ctx</c> object: plain arrays/objects of primitives mirroring the
        /// <see cref="InsightContext"/>. Each file/job/pattern item carries a precomputed, correctly
        /// escaped <c>query</c> string (and patterns a member-expanded one), so scripts don't need to
        /// know the query grammar - they just copy the relevant item's <c>query</c> into their finding.
        /// No CLR objects or host functions are exposed.
        /// </summary>
        private JsValue BuildContextView(Engine engine, InsightContext ctx)
        {
            var root = new JsObject(engine);
            root.FastSetDataProperty("total", ctx.Total);
            root.FastSetDataProperty("info", ctx.Info);
            root.FastSetDataProperty("warn", ctx.Warn);
            root.FastSetDataProperty("error", ctx.Error);
            root.FastSetDataProperty("debug", ctx.Debug);
            root.FastSetDataProperty("minTicks", ctx.MinTicks ?? 0);
            root.FastSetDataProperty("maxTicks", ctx.MaxTicks ?? 0);

            // flags: [{name, count, query}]
            root.FastSetDataProperty("flags", ToArray(engine, ctx.Flags, (o, f) =>
            {
                o.FastSetDataProperty("name", f.Name ?? "");
                o.FastSetDataProperty("count", f.Count);
                o.FastSetDataProperty("query", string.IsNullOrEmpty(f.Name) ? "" : "flag:" + f.Name);
            }));

            // files: [{path, displayName, count, query}]  (query = file:"...")
            root.FastSetDataProperty("files", ToArray(engine, ctx.Files, (o, f) =>
            {
                o.FastSetDataProperty("path", f.Path ?? "");
                o.FastSetDataProperty("displayName", f.DisplayName ?? "");
                o.FastSetDataProperty("count", f.Count);
                o.FastSetDataProperty("query", FileDisplay.DrillDownQuery(f.Path ?? ""));
            }));

            // jobs: [{jobId, entryCount, warn, error, durationMs, firstTicks, lastTicks, query}]
            root.FastSetDataProperty("jobs", ToArray(engine, ctx.Jobs, (o, j) =>
            {
                o.FastSetDataProperty("jobId", j.JobId ?? "");
                o.FastSetDataProperty("entryCount", j.EntryCount);
                o.FastSetDataProperty("warn", j.WarnCount);
                o.FastSetDataProperty("error", j.ErrorCount);
                o.FastSetDataProperty("durationMs", j.Duration.TotalMilliseconds);
                o.FastSetDataProperty("firstTicks", j.FirstSeen.Ticks);
                o.FastSetDataProperty("lastTicks", j.LastSeen.Ticks);
                o.FastSetDataProperty("query", string.IsNullOrEmpty(j.JobId) ? "" : "job:" + j.JobId);
            }));

            // patterns: [{template, count, info, warn, error, debug, firstTicks, lastTicks, query}]
            // query is member-expanded (tmpl:"a" OR tmpl:"b" ...) so hiding/drilling a folded pattern
            // covers all the regex-templates it represents.
            root.FastSetDataProperty("patterns", ToArray(engine, ctx.Patterns, (o, p) =>
            {
                o.FastSetDataProperty("template", p.Template ?? "");
                o.FastSetDataProperty("count", p.Count);
                o.FastSetDataProperty("info", p.Info);
                o.FastSetDataProperty("warn", p.Warn);
                o.FastSetDataProperty("error", p.Error);
                o.FastSetDataProperty("debug", p.Debug);
                o.FastSetDataProperty("firstTicks", p.FirstSeen.Ticks);
                o.FastSetDataProperty("lastTicks", p.LastSeen.Ticks);
                o.FastSetDataProperty("query", PatternQuery.DrillDownQuery(
                    p.MemberTemplates ?? new[] { p.Template }));
            }));

            // workflows: [{name, startTicks, endTicks, durationMs, incomplete, operationCount, sourceFile,
            //             query, operations:[{index, total, name, startTicks, endTicks, durationMs,
            //             incomplete, waitedOnJobId, query}]}]
            // A workflow/operation `query` scopes to the workflow's full time range (timestamp:[start TO
            // end]) - the same drill the Jobs/Workflows tab uses - so worker-job context isn't lost.
            root.FastSetDataProperty("workflows", ToArray(engine, ctx.Workflows, (o, w) =>
            {
                string wfQuery = WorkflowTimeRangeQuery(w.Start, w.End);
                o.FastSetDataProperty("name", w.Name ?? "");
                o.FastSetDataProperty("startTicks", w.Start.Ticks);
                o.FastSetDataProperty("endTicks", w.End.Ticks);
                o.FastSetDataProperty("durationMs", w.Duration.TotalMilliseconds);
                o.FastSetDataProperty("incomplete", w.Incomplete);
                o.FastSetDataProperty("operationCount", w.OperationCount);
                o.FastSetDataProperty("sourceFile", w.SourceFile ?? "");
                o.FastSetDataProperty("query", wfQuery);

                var ops = new JsArray(engine);
                if (w.Operations != null)
                {
                    foreach (var op in w.Operations)
                    {
                        var oo = new JsObject(engine);
                        oo.FastSetDataProperty("index", op.Index);
                        oo.FastSetDataProperty("total", op.Total);
                        oo.FastSetDataProperty("name", op.Name ?? "");
                        oo.FastSetDataProperty("startTicks", op.Start.Ticks);
                        oo.FastSetDataProperty("endTicks", op.End.Ticks);
                        oo.FastSetDataProperty("durationMs", op.Duration.TotalMilliseconds);
                        oo.FastSetDataProperty("incomplete", op.Incomplete);
                        oo.FastSetDataProperty("waitedOnJobId", op.WaitedOnJobIdPrefix ?? "");
                        // Exact warn/error counts within the operation's own [Start,End] window
                        // (populated by the insights pass). 0 for operations with no measurable window.
                        oo.FastSetDataProperty("warns", op.WarnCount);
                        oo.FastSetDataProperty("errors", op.ErrorCount);
                        // Operations jump into the workflow's range (then a script's positionTicks can
                        // land the view on the operation's own start).
                        oo.FastSetDataProperty("query", wfQuery);
                        oo.PreventExtensions();
                        ops.Push(oo);
                    }
                }
                o.FastSetDataProperty("operations", ops);
            }));

            // timeline: { buckets, minTicks, maxTicks, info:[...], warn:[...], error:[...] }
            // Coarse per-bucket level counts over the whole window (same series the chart/spike detectors
            // use). Lets a script correlate a time window (e.g. an operation's span) with error/warn
            // volume. Counts are bucket-approximate; bucket b covers minTicks + b*(span/(buckets-1)).
            root.FastSetDataProperty("timeline", BuildTimelineView(engine, ctx.Timeline));

            // workerTimeouts: { total, totalSeconds, byJob:[{ jobId, count, seconds, query }] }
            // Nuix Worker Watchdog timeouts, aggregated corpus-wide and per worker job. Each per-job
            // `query` isolates that job's timeout lines. Absent (undefined) when there were none.
            if (ctx.WorkerTimeouts != null)
            {
                root.FastSetDataProperty("workerTimeouts", BuildWorkerTimeoutsView(engine, ctx.WorkerTimeouts));
            }

            root.PreventExtensions();
            return root;
        }

        // The query fragment isolating worker-watchdog timeout lines (content phrase, quoted).
        private const string WorkerTimeoutContentQuery = "content:\"has not progressed within timeout\"";

        /// <summary>Builds the read-only workerTimeouts object (see BuildContextView for the shape).</summary>
        private static JsValue BuildWorkerTimeoutsView(Engine engine, InsightContext.WorkerTimeoutStats wt)
        {
            var o = new JsObject(engine);
            o.FastSetDataProperty("total", wt.TotalCount);
            o.FastSetDataProperty("totalSeconds", wt.TotalSeconds);

            var byJob = new JsArray(engine);
            if (wt.ByJob != null)
            {
                foreach (var j in wt.ByJob)
                {
                    var jo = new JsObject(engine);
                    jo.FastSetDataProperty("jobId", j.JobId ?? "");
                    jo.FastSetDataProperty("count", j.Count);
                    jo.FastSetDataProperty("seconds", j.Seconds);
                    jo.FastSetDataProperty("workerCount", j.WorkerCount);
                    // Isolate this job's timeout lines: job:<id> AND <timeout phrase>. When the job id is
                    // unknown (non-worker path), fall back to the corpus-wide timeout query.
                    jo.FastSetDataProperty("query", string.IsNullOrEmpty(j.JobId)
                        ? WorkerTimeoutContentQuery
                        : "job:" + j.JobId + " AND " + WorkerTimeoutContentQuery);
                    jo.PreventExtensions();
                    byJob.Push(jo);
                }
            }
            o.FastSetDataProperty("byJob", byJob);
            o.PreventExtensions();
            return o;
        }

        /// <summary>The workflow drill query: everything within the workflow's wall-clock span.</summary>
        private static string WorkflowTimeRangeQuery(DateTime start, DateTime end)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "timestamp:[{0} TO {1}]", start.Ticks, end.Ticks);
        }

        /// <summary>Builds the read-only timeline object; empty arrays + zero buckets when no series.</summary>
        private static JsValue BuildTimelineView(Engine engine, LogSearchIndex.TimeSeries ts)
        {
            var t = new JsObject(engine);
            int n = (ts != null && ts.Counts != null) ? ts.Buckets : 0;
            var info = new JsArray(engine);
            var warn = new JsArray(engine);
            var error = new JsArray(engine);
            for (int b = 0; b < n; b++)
            {
                info.Push(ts.Counts[b, 0]);   // level 0 = INFO
                warn.Push(ts.Counts[b, 1]);   // level 1 = WARN
                error.Push(ts.Counts[b, 2]);  // level 2 = ERROR
            }
            t.FastSetDataProperty("buckets", n);
            t.FastSetDataProperty("minTicks", (ts != null && ts.MinTicks.HasValue) ? ts.MinTicks.Value : 0);
            t.FastSetDataProperty("maxTicks", (ts != null && ts.MaxTicks.HasValue) ? ts.MaxTicks.Value : 0);
            t.FastSetDataProperty("info", info);
            t.FastSetDataProperty("warn", warn);
            t.FastSetDataProperty("error", error);
            t.PreventExtensions();
            return t;
        }

        private static JsArray ToArray<T>(Engine engine, IReadOnlyList<T> items, Action<JsObject, T> fill)
        {
            var arr = new JsArray(engine);
            if (items != null)
            {
                foreach (var item in items)
                {
                    var o = new JsObject(engine);
                    fill(o, item);
                    o.PreventExtensions();
                    arr.Push(o);
                }
            }
            return arr;
        }

        /// <summary>Turns the JS array returned by analyze() into Insight records; tolerant of junk.</summary>
        private IEnumerable<Insight> MaterializeFindings(JsValue result)
        {
            var findings = new List<Insight>();
            if (result == null || !result.IsArray()) { return findings; }

            var arr = result.AsArray();
            for (uint i = 0; i < arr.Length; i++)
            {
                var item = arr[i];
                if (item == null || !item.IsObject()) { continue; }
                var o = item.AsObject();

                string title = GetString(o, "title");
                if (string.IsNullOrWhiteSpace(title)) { continue; } // a finding must at least have a title

                findings.Add(new Insight
                {
                    Severity = ParseSeverity(GetString(o, "severity")),
                    Title = title,
                    Detail = GetString(o, "detail"),
                    Query = GetString(o, "query"),
                    PositionEntryId = GetLong(o, "positionEntryId"),
                    PositionTicks = GetLong(o, "positionTicks"),
                });
            }
            return findings;
        }

        private static string GetString(ObjectInstance o, string prop)
        {
            var v = o.Get(prop);
            return (v != null && v.IsString()) ? v.AsString() : null;
        }

        private static long? GetLong(ObjectInstance o, string prop)
        {
            var v = o.Get(prop);
            if (v != null && v.IsNumber())
            {
                double d = v.AsNumber();
                if (!double.IsNaN(d) && !double.IsInfinity(d)) { return (long)d; }
            }
            return null;
        }

        private static InsightSeverity ParseSeverity(string s)
        {
            if (string.IsNullOrEmpty(s)) { return InsightSeverity.Notice; }
            switch (s.Trim().ToLowerInvariant())
            {
                case "info": return InsightSeverity.Info;
                case "warning": case "warn": return InsightSeverity.Warning;
                case "critical": case "crit": return InsightSeverity.Critical;
                default: return InsightSeverity.Notice;
            }
        }
    }
}
