using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class WorkerStateChangeClassifier : IEntryClassifier
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
    }
}
