using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags engine/scheduler communication failures: the server cannot query an engine, cannot
    /// handle an operation, or cannot reach the scheduler.
    /// </summary>
    public class EngineCommunicationFailureClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            string content = entry.Content ?? "";
            if (content.Contains("Cannot query Server")
                || content.Contains("Cannot handle operation")
                || content.Contains("Cannot connect to Scheduler"))
            {
                return new[] { "engine_comm_failure" };
            }
            return null;
        }
    }
}
