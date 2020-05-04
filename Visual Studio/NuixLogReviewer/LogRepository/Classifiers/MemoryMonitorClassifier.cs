using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class MemoryMonitorClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Look for entries that are coming from Nuix memory monitor which should
            // include things like garbage collection notifications
            if (entry.Source.Contains("com.nuix.monitoring.memory"))
            {
                return new string[] { "memory" };
            }
            else
            {
                return null;
            }
        }
    }
}
