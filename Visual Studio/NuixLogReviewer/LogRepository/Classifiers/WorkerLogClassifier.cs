using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class WorkerLogClassifier : IEntryClassifier
    {
        private static Regex jobIdRegex = new Regex(@"job-[a-f0-9]{32}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Mark entries from a filed that apper to come from a log file that is nested in a job folder
            // created by a worker process.
            if (entry.FilePath.Contains("job-") && jobIdRegex.IsMatch(entry.FilePath))
            {
                return new string[] { "worker_log" };
            }
            else
            {
                return null;
            }
        }
    }
}
