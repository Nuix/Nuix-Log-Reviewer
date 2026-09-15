using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags entries where an OIDC UserService synchronization failed, e.g.
    /// "Could not synchronize UserService OIDC ...". Common in Automate identity/SSO issues.
    /// </summary>
    public class OidcSyncFailureClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content != null && entry.Content.Contains("Could not synchronize UserService OIDC"))
            {
                return new[] { "oidc_sync_failure" };
            }
            return null;
        }
    }
}
