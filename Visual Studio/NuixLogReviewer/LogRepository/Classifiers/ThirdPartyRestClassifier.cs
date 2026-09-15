using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags entries involving a third-party REST integration failure (ThirdPartyRestException),
    /// which appears in either the source or the message content.
    /// </summary>
    public class ThirdPartyRestClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if ((entry.Content != null && entry.Content.Contains("ThirdPartyRestException"))
                || (entry.Source != null && entry.Source.Contains("ThirdPartyRestException")))
            {
                return new[] { "third_party_rest" };
            }
            return null;
        }
    }
}
