using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>
    /// Flags licensing-related entries: those emitted by the Automate license resource, or reporting
    /// a licence session that could not be found.
    /// </summary>
    public class LicenseClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if ((entry.Source != null && entry.Source.Contains("AutomateLicenseResource"))
                || (entry.Content != null && entry.Content.Contains("LicenceSessionNotFoundException")))
            {
                return new[] { "license" };
            }
            return null;
        }
    }
}
