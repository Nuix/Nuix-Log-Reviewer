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

        /// <summary>Oldest event time in the matched set, or null if empty.</summary>
        public DateTime? FilteredMinTime { get; set; }

        /// <summary>Newest event time in the matched set, or null if empty.</summary>
        public DateTime? FilteredMaxTime { get; set; }

        /// <summary>
        /// Count of matched entries carrying each classifier flag, for the current filtered set.
        /// Keyed by flag name (e.g. "oidc_sync_failure"). Only flags present in the loaded data
        /// are included; entries are omitted or zero when the flag doesn't occur in the result set.
        /// </summary>
        public IDictionary<string, int> FlagCounts { get; set; } = new Dictionary<string, int>();

        public LogEntrySearchResponse(IItemsProvider<NuixLogEntry> itemsProvider, int pageSize) : base(itemsProvider, pageSize)
        {
        }
    }
}
