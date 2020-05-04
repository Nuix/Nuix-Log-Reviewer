using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    class ScriptOutputClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Look for entries that appear to be script related
            if (entry.Channel == "SCRIPT")
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
