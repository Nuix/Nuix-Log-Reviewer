using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class ScriptOutputClassifier : IEntryClassifier, IClassifierDescriptions
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

        public IEnumerable<ClassifierFlagDescription> GetFlagDescriptions()
        {
            return new[]
            {
                new ClassifierFlagDescription("script",
                    "Output emitted by a user script (the entry's source begins with SCRIPT)."),
            };
        }
    }
}
