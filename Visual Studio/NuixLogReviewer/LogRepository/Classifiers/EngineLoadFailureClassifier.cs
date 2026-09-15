using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags engine startup/load failures: unable to load Engine libraries or the ClassLoader.
    /// </summary>
    public class EngineLoadFailureClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            string content = entry.Content ?? "";
            if (content.Contains("Cannot load Engine libraries")
                || content.Contains("Cannot load ClassLoader"))
            {
                return new[] { "engine_load_failure" };
            }
            return null;
        }
    }
}
