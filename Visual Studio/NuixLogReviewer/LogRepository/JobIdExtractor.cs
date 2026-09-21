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
