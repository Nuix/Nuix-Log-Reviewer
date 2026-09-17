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
    }
}
