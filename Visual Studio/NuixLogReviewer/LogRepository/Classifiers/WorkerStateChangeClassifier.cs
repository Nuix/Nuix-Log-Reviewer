using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class WorkerStateChangeClassifier : IEntryClassifier, IClassifierDescriptions
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Look for entries like: Changing state from PENDING to RUNNING
            if (entry.Content.Contains("Changing state"))
            {
                return new string[] { "worker_state" };
            }
            else
            {
                return null;
            }
        }

        public IEnumerable<ClassifierFlagDescription> GetFlagDescriptions()
        {
            return new[]
            {
                new ClassifierFlagDescription("worker_state",
                    "A worker changed lifecycle state (e.g. \"Changing state from PENDING to RUNNING\")."),
            };
        }
    }
}
