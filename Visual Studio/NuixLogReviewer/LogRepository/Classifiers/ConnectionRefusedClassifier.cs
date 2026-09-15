using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags entries where an outbound connection was refused (HttpHostConnectException /
    /// "Connection refused"). Indicates a dependency/service was unreachable.
    /// </summary>
    public class ConnectionRefusedClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            string content = entry.Content ?? "";
            if (content.Contains("HttpHostConnectException") || content.Contains("Connection refused"))
            {
                return new[] { "connection_refused" };
            }
            return null;
        }
    }
}
