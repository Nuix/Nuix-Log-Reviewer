using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class MultiLineClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content.Contains("\n") || entry.Content.Contains("\r"))
            {
                return new string[] { "multi_line" };
            }
            else
            {
                return null;
            }
        }
    }
}
