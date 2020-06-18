using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class KillingCurrentProcessClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if(entry.Content.StartsWith("Killing current process...", StringComparison.OrdinalIgnoreCase))
            {
                return new string[] { "process_killed" };
            } else
            {
                return null;
            }
        }
    }
}
