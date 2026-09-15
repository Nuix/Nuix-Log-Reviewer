using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>Flags entries reporting an HTTP 401 Unauthorized response.</summary>
    public class HttpUnauthorizedClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content != null && entry.Content.Contains("HTTP/401"))
            {
                return new[] { "http_unauthorized" };
            }
            return null;
        }
    }
}
