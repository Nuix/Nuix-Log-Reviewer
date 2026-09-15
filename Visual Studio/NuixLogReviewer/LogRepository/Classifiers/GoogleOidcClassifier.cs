using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags entries originating from the Google OIDC / Google user-service code paths, e.g.
    /// GoogleOidcUserServiceClient or GoogleUserServiceWorker. Useful for isolating Google
    /// identity / Vault integration activity.
    /// </summary>
    public class GoogleOidcClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            string source = entry.Source ?? "";
            if (source.Contains("oidc.google")
                || source.Contains("GoogleOidcUserServiceClient")
                || source.Contains("GoogleUserServiceWorker"))
            {
                return new[] { "google_oidc" };
            }
            return null;
        }
    }
}
