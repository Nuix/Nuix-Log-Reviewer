using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>Flags entries reporting a socket/HTTP read timeout (SocketTimeoutException).</summary>
    public class HttpTimeoutClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content != null && entry.Content.Contains("SocketTimeoutException"))
            {
                return new[] { "http_timeout" };
            }
            return null;
        }
    }
}
