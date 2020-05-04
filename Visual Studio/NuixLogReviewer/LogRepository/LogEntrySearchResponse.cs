using DataVirtualization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class LogEntrySearchResponse : VirtualizingCollection<NuixLogEntry>
    {
        public int InfoEntryCount { get; set; }
        public int WarnEntryCount { get; set; }
        public int ErrorEntryCount { get; set; }
        public int DebugEntryCount { get; set; }

        public LogEntrySearchResponse(IItemsProvider<NuixLogEntry> itemsProvider, int pageSize) : base(itemsProvider, pageSize)
        {
        }
    }
}
