using System.Text.RegularExpressions;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Extracts the Nuix worker "job" identifier from a log file path.
    ///
    /// Worker-based jobs (Nuix engine / Workstation) create a job-level folder named
    /// "job-&lt;32 hex&gt;". Inside it each worker keeps its own subfolder, which may itself contain
    /// one or more restart subfolders if the worker process died and relaunched:
    ///
    ///     ...\job-&lt;32hex&gt;\&lt;worker&gt;\[&lt;restart&gt;\]...&lt;worker&gt;.log
    ///
    /// The job id lives only at the job-level folder, so we take the FIRST "job-&lt;32hex&gt;" match in
    /// the path - that is always the job folder regardless of the worker/restart nesting below it.
    /// This is the single source of truth for "which job does this entry belong to", shared by the
    /// worker-log classifier and the job_dv indexing so they can never drift apart.
    /// </summary>
    public static class JobIdExtractor
    {
        // "job-" followed by a 32-char hex id (the worker job folder). Case-insensitive; the first
        // match in a path is the job-level folder.
        private static readonly Regex JobIdRegex =
            new Regex(@"job-[a-f0-9]{32}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns the lower-cased "job-&lt;32hex&gt;" id for the given file path, or null if the path
        /// isn't within a worker job folder. Lower-casing keeps ids stable for grouping and search.
        /// </summary>
        public static string Extract(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            var m = JobIdRegex.Match(filePath);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        /// <summary>True if the path is within a worker job folder.</summary>
        public static bool IsWorkerLog(string filePath) => Extract(filePath) != null;

        // The worker-level folder sits DIRECTLY below the "job-<hex>" folder, e.g.
        // ".../job-<hex>/worker-1/<restart-timestamp>/nuix.log" -> "worker-1". Capture the job folder
        // plus the single segment immediately after it; the restart-timestamp folder(s) below are the
        // SAME worker relaunching, not a new worker, so they're excluded from the worker identity.
        private static readonly Regex WorkerSegmentRegex =
            new Regex(@"job-[a-f0-9]{32}[\\/]+(?<worker>[^\\/]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns a stable per-WORKER identity for a worker-job log path: the "job-&lt;hex&gt;/&lt;worker&gt;"
        /// pair (lower-cased), where &lt;worker&gt; is the single folder directly beneath the job folder
        /// (typically "worker-0", "worker-1", ...). Restart/run subfolders below the worker folder are
        /// deliberately ignored, so a worker that died and relaunched still counts as ONE worker. Returns
        /// null when the path isn't inside a worker job folder, or when the job folder has no child
        /// segment (e.g. the job folder directly holds a file). Used to count distinct workers per job.
        /// </summary>
        public static string WorkerId(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            var m = WorkerSegmentRegex.Match(filePath);
            if (!m.Success) return null;
            // Rebuild as "job-<hex>/<worker>" lower-cased so the id is unique across jobs and matches the
            // lower-cased file_dv values the index stores.
            string worker = m.Groups["worker"].Value;
            // Guard against matching the file itself when the job folder directly contains a file with no
            // worker subfolder (worker would then be the file name). That's not a worker lane; skip it by
            // requiring the matched segment to be followed by another separator in the original path.
            int segEnd = m.Index + m.Length;
            if (segEnd >= filePath.Length) return null; // nothing after the segment => it was the file name
            char next = filePath[segEnd];
            if (next != '\\' && next != '/') return null;
            return m.Value.ToLowerInvariant();
        }

        // The worker (and optional restart) folder(s) that sit BELOW the "job-<hex>" folder, e.g.
        // ".../job-<hex>/engine 01a/worker-1/nuix.log" -> "engine 01a/worker-1". This is the per-worker
        // attribution the job id alone doesn't carry, used to answer "one worker, some, or all?".
        private static readonly Regex JobFolderRegex =
            new Regex(@"job-[a-f0-9]{32}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns a human-readable worker label derived from the path: the folder segment(s) between
        /// the "job-&lt;hex&gt;" folder and the log file (e.g. "engine 01a/worker-1"). Falls back to the
        /// containing folder name for non-job logs, or "" if none can be derived. Normalizes separators
        /// to '/'. Used to group a pattern's entries by which worker produced them.
        /// </summary>
        public static string WorkerLabel(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            string norm = filePath.Replace('\\', '/');
            var m = JobFolderRegex.Match(norm);
            if (m.Success)
            {
                int afterJob = m.Index + m.Length;
                // Take everything after the job folder, drop the trailing file name.
                string tail = norm.Substring(afterJob).TrimStart('/');
                int lastSlash = tail.LastIndexOf('/');
                string folders = lastSlash > 0 ? tail.Substring(0, lastSlash) : "";
                if (folders.Length > 0) return folders;
            }
            // Non-worker log (or job folder directly holds the file): use the immediate parent folder.
            int fileSlash = norm.LastIndexOf('/');
            if (fileSlash <= 0) return "";
            string parentPath = norm.Substring(0, fileSlash);
            int parentSlash = parentPath.LastIndexOf('/');
            return parentSlash >= 0 ? parentPath.Substring(parentSlash + 1) : parentPath;
        }
    }
}
