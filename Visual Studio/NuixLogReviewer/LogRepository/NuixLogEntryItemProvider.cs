using NuixLogReviewer.LogRepository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewerObjects
{
    public class NuixLogEntryItemProvider : DataVirtualization.IItemsProvider<NuixLogEntry>
    {
        public NuixLogRepo SourceRepository { get; set; }
        public IList<long> Ids { get; set; }

        public int FetchCount()
        {
            return Ids.Count;
        }

        public IList<NuixLogEntry> FetchRange(int startIndex, int count)
        {
            // Pull one EXTRA leading id (the row just before this page) when we're not at the very
            // start, so the first row of the page gets a correct "gap to previous" across the page
            // boundary. Ids is time-sorted, so a contiguous id-slice is also the display order.
            bool hasLeading = startIndex > 0;
            int fetchStart = hasLeading ? startIndex - 1 : startIndex;
            int fetchCount = hasLeading ? count + 1 : count;

            var fetched = SourceRepository.Database
                .ReadEntries(Ids.Skip(fetchStart).Take(fetchCount))
                .ToList();

            // Stamp each entry's gap to its chronological predecessor within the fetched sequence.
            // The first fetched entry's gap is left as whatever the leading row provides: if we pulled
            // a leading row, entry[0] is that predecessor (dropped below) and entry[1]'s gap is real;
            // if not (page starts at index 0), entry[0] is the very first row => null gap.
            for (int i = 1; i < fetched.Count; i++)
            {
                fetched[i].GapToPrevious = fetched[i].TimeStamp - fetched[i - 1].TimeStamp;
            }
            if (!hasLeading && fetched.Count > 0)
            {
                fetched[0].GapToPrevious = null; // very first row of the whole set
            }

            // Drop the extra leading row so the page contains exactly the requested range.
            if (hasLeading && fetched.Count > 0)
            {
                fetched.RemoveAt(0);
            }

            return fetched;
        }
    }
}