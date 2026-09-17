using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class WorkerLogClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Mark entries whose log file lives within a worker job folder (job-<32hex>). Uses the
            // shared JobIdExtractor so this flag and the indexed job_dv always agree on what a
            // worker log is and which job it belongs to.
            if (JobIdExtractor.IsWorkerLog(entry.FilePath))
            {
                return new string[] { "worker_log" };
            }
            return null;
        }
    }
}
