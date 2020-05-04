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
            return SourceRepository.Database.ReadEntries(Ids.Skip(startIndex).Take(count)).ToList();
        }
    }
}