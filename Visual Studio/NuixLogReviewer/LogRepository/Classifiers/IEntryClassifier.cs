using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public interface IEntryClassifier
    {
        /// <summary>
        /// Can analyze a given log entry and decide to return a series
        /// of flags that will be stored in the index for the item.  Implementations are
        /// best off quickly skipping entries they don't care about (by returning empty/null result) if possible before doing
        /// more heavy analysis of a given entry to keep load file import speeds reasonable.
        /// </summary>
        /// <param name="entry">A log entry to be analyzed and possibly classified.</param>
        /// <returns>A collection of 0 or more strings which will be recorded as flags on the item for later searching.</returns>
        IEnumerable<string> Classify(NuixLogEntry entry);
    }
}
