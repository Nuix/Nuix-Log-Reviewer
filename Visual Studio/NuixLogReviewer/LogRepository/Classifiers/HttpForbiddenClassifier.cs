using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>Flags entries reporting an HTTP 403 Forbidden response.</summary>
    public class HttpForbiddenClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content != null && entry.Content.Contains("HTTP/403"))
            {
                return new[] { "http_forbidden" };
            }
            return null;
        }
    }
}
