using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class ProblemProcessingItemClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if(entry.Content.StartsWith("Problem processing item", StringComparison.OrdinalIgnoreCase))
            {
                return new String[] { "problem_processing_item" };
            } else
            {
                return null;
            }
        }
    }
}
