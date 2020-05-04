using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Provides a way for NuixLogRepo to broadcast status messages and progress back to the UI
    /// </summary>
    public class ProgressBroadcaster
    {
        public delegate void StatusUpdatedDel(string statusMessage);
        public event StatusUpdatedDel StatusUpdated;
        public void BroadcastStatus(string statusMessage) { if (StatusUpdated != null) StatusUpdated(statusMessage); }

        public delegate void ProgressUpdatedDel(long progress);
        public event ProgressUpdatedDel ProgressUpdated;
        public void BroadcastProgress(long progress) { if (ProgressUpdated != null) ProgressUpdated(progress); }
    }
}
