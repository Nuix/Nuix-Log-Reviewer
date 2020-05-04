using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class DiskSpaceMessageClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Look for messages regarding disk space
            if (entry.Source.ToLower().Contains("Disk space message received"))
            {
                return new string[] { "disk_space" };
            }
            else
            {
                return null;
            }
        }
    }
}
