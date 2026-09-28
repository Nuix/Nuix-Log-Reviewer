using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Reconstructs Nuix Automate "workflow executions" (and their operation breakdown) from engine-log
    /// entries. Unlike worker Jobs — which are derived from the <c>job-&lt;32hex&gt;</c> folder in a file
    /// path (see <see cref="JobIdExtractor"/>) — a workflow execution is a higher-level Automate concept
    /// that isn't path-encoded: it's delineated by lifecycle lines the engine logs from the
    /// <c>WorkflowExecution</c> component. A single workflow execution may launch zero or more worker
    /// jobs (e.g. an "Add Evidence" operation runs a worker-based ingestion), so on the timeline a
    /// workflow is drawn as the container track ABOVE the job lanes.
    ///
    /// Markers (all from source <c>...core.execution.workflow.WorkflowExecution</c>):
    /// <list type="bullet">
    /// <item><c>Starting execution of workflow &lt;name&gt; on &lt;date&gt;</c> — opens an execution.</item>
    /// <item><c>Execution finished on &lt;date&gt;</c> — closes it.</item>
    /// <item><c>Starting operation N/M &lt;name&gt; on &lt;date&gt;</c> — opens operation N.</item>
    /// <item><c>Operation N/M &lt;name&gt; completed on &lt;date&gt;</c> — closes operation N.</item>
    /// </list>
    /// Plus, from source <c>...core.utils.nuix.NuixUtils</c>, the explicit workflow&#8596;job link:
    /// <c>Waiting for Nuix Engine Job &lt;id&gt;</c> / <c>Done waiting for Nuix Engine Job &lt;id&gt;</c>.
    /// This is a REAL identifier link (not timing coincidence), so the operation that spawned a worker
    /// job can be tied to that job's id with certainty.
    ///
    /// Durations use each entry's own timestamp (accurate to the millisecond), not the coarse
    /// minute-precision "on &lt;date&gt;" text in the message. Executions are scoped PER engine-log file
    /// so two engine logs never cross-pair. Kept free of WPF/DB types for direct testing.
    /// </summary>
    public static class WorkflowExecutionMiner
    {
        public const string WorkflowSource = "com.nuix.automate.workflow.core.execution.workflow.WorkflowExecution";
        public const string NuixUtilsSource = "com.nuix.automate.workflow.core.utils.nuix.NuixUtils";

        // "Starting execution of workflow <name> on <date>" — capture <name> (non-greedy up to " on ").
        private static readonly Regex StartExec = new Regex(
            @"^Starting execution of workflow (?<name>.+?)(?: on .*)?$", RegexOptions.Compiled);
        private static readonly Regex FinishExec = new Regex(
            @"^Execution finished\b", RegexOptions.Compiled);

        // "Starting operation N/M <name> on <date>" and "Operation N/M <name> completed on <date>".
        private static readonly Regex StartOp = new Regex(
            @"^Starting operation (?<n>\d+)/(?<m>\d+) (?<name>.+?)(?: on .*)?$", RegexOptions.Compiled);
        private static readonly Regex CompleteOp = new Regex(
            @"^Operation (?<n>\d+)/(?<m>\d+) (?<name>.+?) completed(?: on .*)?$", RegexOptions.Compiled);

        // "Waiting for Nuix Engine Job <id...>" / "Done waiting for Nuix Engine Job <id...>". The engine
        // logs the FULL job GUID in hyphenated form (e.g. 1a2b3c4d-5e6f-7890-abcd-ef1234567890); capture
        // hex+hyphens so we get the whole id, not just the first group before a hyphen.
        private static readonly Regex WaitJob = new Regex(
            @"\bWaiting for Nuix Engine Job (?<id>[A-Fa-f0-9\-]+)", RegexOptions.Compiled);
        private static readonly Regex DoneWaitJob = new Regex(
            @"\bDone waiting for Nuix Engine Job (?<id>[A-Fa-f0-9\-]+)", RegexOptions.Compiled);

        /// <summary>One operation within a workflow execution.</summary>
        public sealed class OperationInfo
        {
            public int Index { get; set; }            // N in "N/M"
            public int Total { get; set; }            // M in "N/M"
            public string Name { get; set; }
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public bool Incomplete { get; set; }      // started but never observed to complete
            public TimeSpan Duration => End - Start;

            /// <summary>
            /// Worker engine-job id this operation waited on, if any (a real link, straight from the
            /// engine log). Stored as logged (hyphenated GUID, e.g. "1a2b3c4d-5e6f-7890-abcd-ef1234567890").
            /// </summary>
            public string WaitedOnJobIdPrefix { get; set; }

            /// <summary>
            /// The waited-on job id normalized for matching against a worker job id: hyphens removed and
            /// lower-cased (so "1a2b3c4d-5e6f-..." matches the "job-1a2b3c4d5e6f..." folder id). "" when none.
            /// </summary>
            public string WaitedOnJobIdNormalized =>
                string.IsNullOrEmpty(WaitedOnJobIdPrefix) ? "" : WaitedOnJobIdPrefix.Replace("-", "").ToLowerInvariant();

            /// <summary>
            /// The full worker job id (e.g. "job-1a2b3c4d...") this operation's wait was resolved to,
            /// once jobs and workflows are computed together and a UNIQUE job matches
            /// <see cref="WaitedOnJobIdNormalized"/>. Null when unresolved/ambiguous. Set by the UI.
            /// </summary>
            public string ResolvedJobId { get; set; }

            /// <summary>True when this operation is confirmed linked to a specific worker job.</summary>
            public bool HasResolvedJob => !string.IsNullOrEmpty(ResolvedJobId);

            /// <summary>Display text for the linked job column: "job &lt;id&gt;" or "" when none (so the
            /// column is blank rather than a bare "job " for operations that didn't wait on a worker job).</summary>
            public string WaitedOnJobText =>
                string.IsNullOrEmpty(WaitedOnJobIdPrefix) ? "" : "job " + WaitedOnJobIdPrefix;

            /// <summary>Action-oriented label for the clickable job link, so its purpose is obvious.</summary>
            public string WaitedOnJobLinkText =>
                string.IsNullOrEmpty(WaitedOnJobIdPrefix) ? "" : "\u21B3 highlight worker job " + WaitedOnJobIdPrefix;

            public string DurationText => FormatDuration(End - Start);
        }

        /// <summary>A single workflow execution with its operation breakdown.</summary>
        public sealed class WorkflowInfo
        {
            public string Name { get; set; }
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public bool Incomplete { get; set; }      // started but never observed to finish (crash/truncated log)
            public string SourceFile { get; set; }    // engine-log path this execution came from
            public List<OperationInfo> Operations { get; set; } = new List<OperationInfo>();
            public List<long> Ids { get; set; } = new List<long>();   // contributing entry ids (for drill-down)

            public TimeSpan Duration => End - Start;
            public string DurationText => FormatDuration(End - Start);
            public int OperationCount => Operations.Count;

            /// <summary>Distinct worker engine-job id prefixes this workflow's operations waited on.</summary>
            public IReadOnlyList<string> LinkedJobIdPrefixes =>
                Operations.Where(o => !string.IsNullOrEmpty(o.WaitedOnJobIdPrefix))
                          .Select(o => o.WaitedOnJobIdPrefix).Distinct().ToList();
        }

        /// <summary>
        /// Mines workflow executions from the given entries. Only <see cref="WorkflowSource"/> and
        /// <see cref="NuixUtilsSource"/> entries matter; anything else is ignored, so the caller can pass
        /// a superset. Entries are grouped by source file and processed in timestamp order within each
        /// file. Returns executions ordered by start time ascending.
        /// </summary>
        public static IList<WorkflowInfo> Mine(IEnumerable<NuixLogEntry> entries)
        {
            var result = new List<WorkflowInfo>();
            if (entries == null) return result;

            // Group by source file so two engine logs never cross-pair their lifecycle lines.
            var byFile = entries
                .Where(e => e != null && (e.Source == WorkflowSource || e.Source == NuixUtilsSource))
                .GroupBy(e => e.FilePath ?? "");

            foreach (var file in byFile)
            {
                var ordered = file.OrderBy(e => e.TimeStamp).ThenBy(e => e.LineNumber).ToList();
                MineFile(ordered, file.Key, result);
            }

            result.Sort((a, b) => a.Start.CompareTo(b.Start));
            return result;
        }

        private static void MineFile(List<NuixLogEntry> ordered, string file, List<WorkflowInfo> result)
        {
            WorkflowInfo current = null;
            OperationInfo currentOp = null;
            // Track a pending "Waiting for Nuix Engine Job <id>" so it attaches to the operation in flight.
            string pendingWaitJobId = null;

            foreach (var e in ordered)
            {
                string content = (e.Content ?? "").TrimEnd();

                if (e.Source == NuixUtilsSource)
                {
                    // Associate engine-job waits with the operation currently running.
                    var wm = WaitJob.Match(content);
                    if (wm.Success)
                    {
                        pendingWaitJobId = wm.Groups["id"].Value;
                        if (currentOp != null) currentOp.WaitedOnJobIdPrefix = pendingWaitJobId;
                    }
                    else if (DoneWaitJob.Match(content) is Match dm && dm.Success)
                    {
                        if (currentOp != null && string.IsNullOrEmpty(currentOp.WaitedOnJobIdPrefix))
                            currentOp.WaitedOnJobIdPrefix = dm.Groups["id"].Value;
                        pendingWaitJobId = null;
                    }
                    if (current != null) current.Ids.Add(e.ID);
                    continue;
                }

                // From here: WorkflowSource lifecycle lines.
                var startExec = StartExec.Match(content);
                if (startExec.Success && !content.StartsWith("Starting operation", StringComparison.Ordinal))
                {
                    // Close any prior open execution defensively (a new start implies the last ended).
                    CloseExecution(current, currentOp, ordered, result, ref current, ref currentOp);
                    current = new WorkflowInfo
                    {
                        Name = startExec.Groups["name"].Value.Trim(),
                        Start = e.TimeStamp,
                        End = e.TimeStamp,
                        SourceFile = file,
                    };
                    current.Ids.Add(e.ID);
                    currentOp = null;
                    pendingWaitJobId = null;
                    continue;
                }

                if (current == null)
                {
                    // Operation/finish lines with no open execution: ignore (partial log head).
                    continue;
                }
                current.Ids.Add(e.ID);
                current.End = e.TimeStamp;

                var startOp = StartOp.Match(content);
                if (startOp.Success)
                {
                    // Close a previous still-open op (shouldn't normally happen; ops are sequential).
                    if (currentOp != null && currentOp.End < currentOp.Start) currentOp.End = e.TimeStamp;
                    currentOp = new OperationInfo
                    {
                        Index = int.Parse(startOp.Groups["n"].Value),
                        Total = int.Parse(startOp.Groups["m"].Value),
                        Name = startOp.Groups["name"].Value.Trim(),
                        Start = e.TimeStamp,
                        End = e.TimeStamp,
                        Incomplete = true, // until we see its completion
                    };
                    if (pendingWaitJobId != null) currentOp.WaitedOnJobIdPrefix = pendingWaitJobId;
                    current.Operations.Add(currentOp);
                    continue;
                }

                var completeOp = CompleteOp.Match(content);
                if (completeOp.Success)
                {
                    int n = int.Parse(completeOp.Groups["n"].Value);
                    // Match to the operation with this index (usually currentOp).
                    var op = (currentOp != null && currentOp.Index == n)
                        ? currentOp
                        : current.Operations.LastOrDefault(o => o.Index == n);
                    if (op != null)
                    {
                        op.End = e.TimeStamp;
                        op.Incomplete = false;
                    }
                    currentOp = null;
                    pendingWaitJobId = null;
                    continue;
                }

                if (FinishExec.Match(content).Success)
                {
                    current.End = e.TimeStamp;
                    FinalizeOpenOperation(current, currentOp);
                    result.Add(current);
                    current = null;
                    currentOp = null;
                    pendingWaitJobId = null;
                }
            }

            // A workflow left open at end of file (crash/truncated): close at last-seen time, flag it.
            if (current != null)
            {
                current.Incomplete = true;
                FinalizeOpenOperation(current, currentOp);
                if (ordered.Count > 0) current.End = ordered[ordered.Count - 1].TimeStamp;
                result.Add(current);
            }
        }

        private static void FinalizeOpenOperation(WorkflowInfo wf, OperationInfo op)
        {
            if (op != null && op.Incomplete)
            {
                // Close an unfinished operation at the workflow's end so its bar has a span.
                if (op.End < wf.End) op.End = wf.End;
            }
        }

        // Overload used defensively when a second "Starting execution" appears before the prior finished.
        private static void CloseExecution(WorkflowInfo cur, OperationInfo curOp, List<NuixLogEntry> ordered,
            List<WorkflowInfo> result, ref WorkflowInfo current, ref OperationInfo currentOp)
        {
            if (cur == null) return;
            cur.Incomplete = true;
            FinalizeOpenOperation(cur, curOp);
            result.Add(cur);
            current = null;
            currentOp = null;
        }

        /// <summary>Compact human duration: "5d 16h", "3h 12m", "4m 5s", "45s", "120ms".</summary>
        internal static string FormatDuration(TimeSpan s)
        {
            if (s < TimeSpan.Zero) s = TimeSpan.Zero;
            if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
            if (s.TotalHours >= 1) return $"{s.Hours}h {s.Minutes}m";
            if (s.TotalMinutes >= 1) return $"{s.Minutes}m {s.Seconds}s";
            if (s.TotalSeconds >= 1) return $"{s.Seconds}s";
            return $"{s.Milliseconds}ms";
        }
    }
}
