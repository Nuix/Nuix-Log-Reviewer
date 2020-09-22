using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class ScriptOutputClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Look for entries that appear to be script related
            if (entry.Source.Trim().StartsWith("SCRIPT"))
            {
                return new string[] { "script" };
            }
            else
            {
                return null;
            }
        }
    }
}
