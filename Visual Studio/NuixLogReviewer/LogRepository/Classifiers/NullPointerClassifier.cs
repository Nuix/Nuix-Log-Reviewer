using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>Flags entries containing a Java NullPointerException.</summary>
    public class NullPointerClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content != null && entry.Content.Contains("NullPointerException"))
            {
                return new[] { "null_pointer" };
            }
            return null;
        }
    }
}
