using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class SessionIsClosedClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            if (entry.Content.Contains("The Session is closed"))
            {
                return new string[] { "session_is_closed" };
            }
            else
            {
                return null;
            }
        }
    }
}
