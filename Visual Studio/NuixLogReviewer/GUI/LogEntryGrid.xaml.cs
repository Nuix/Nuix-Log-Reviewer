using NuixLogReviewer.LogRepository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// Interaction logic for LogEntryGrid.xaml
    /// </summary>
    public partial class LogEntryGrid : UserControl
    {
        public IList<NuixLogEntry> CurrentLogEntries
        {
            get; private set;
        }

        /// <summary>The log entry backing the currently selected row, or null.</summary>
        public NuixLogEntry SelectedEntry
        {
            get { return resultsGrid.SelectedItem as NuixLogEntry; }
        }

        public LogEntryGrid()
        {
            InitializeComponent();
            // Observe scrolling so we can report which rows are visible.
            resultsGrid.Loaded += (s, e) =>
            {
                var sv = FindScrollViewer(resultsGrid);
                if (sv != null)
                {
                    sv.ScrollChanged += (a, b) => ReportVisibleRange();
                }
            };
        }

        public void SetLogEntries(IList<NuixLogEntry> entries)
        {
            CurrentLogEntries = entries;
            resultsGrid.ItemsSource = entries;
            // Report the initial visible window once layout settles.
            Dispatcher.BeginInvoke(new Action(ReportVisibleRange), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Selects the row for the entry with the given id and scrolls it into view. Used after a
        /// "pivot around event" search so the entry the user pivoted on is highlighted and centered in
        /// the freshly loaded context. No-op if the id isn't in the current result set.
        /// </summary>
        /// <remarks>
        /// The results are a data-virtualized collection whose own IndexOf/Contains are intentionally
        /// unimplemented (they return -1/false), so we can't let the DataGrid resolve the item itself.
        /// Instead we find the id's ordinal position in the response's ordered id list, materialize that
        /// row through the collection indexer, then select and scroll to it. Deferred to Loaded priority
        /// so the ItemsSource change has been applied and containers can be realized.
        /// </remarks>
        public void SelectAndScrollTo(long id)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                int index = IndexOfEntryId(id);
                if (index < 0) { return; }

                var entries = CurrentLogEntries;
                if (entries == null || index >= entries.Count) { return; }

                // Materialize the row object via the virtualizing collection indexer.
                NuixLogEntry entry = entries[index];
                if (entry == null) { return; }

                resultsGrid.SelectedItem = entry;
                resultsGrid.ScrollIntoView(entry);
                resultsGrid.UpdateLayout();
                // ScrollIntoView aligns to the nearest edge; nudging to the item again after layout
                // gives a stable, roughly-centered view of the pivot row within the loaded context.
                CenterRow(index, entries.Count);
                ReportVisibleRange();

                // Bring keyboard focus onto the selected row's container so the selection reads as the
                // active (not just logical) selection and arrow-key navigation continues from here.
                // Deferred again so the container exists after the centering scroll realizes it.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (resultsGrid.ItemContainerGenerator.ContainerFromItem(entry) is DataGridRow row)
                    {
                        row.Focus();
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Finds the ordinal index of the given entry id within the current result set's ordered id
        /// list, or -1 if not present / unavailable. Handles both the real per-query id list and the
        /// blank-query <see cref="LogRepository.AllEntriesIDList"/> (whose ids are 1..N in order).
        /// </summary>
        private int IndexOfEntryId(long id)
        {
            if (!(CurrentLogEntries is LogRepository.LogEntrySearchResponse response)) { return -1; }
            if (!(response.ItemsProvider is NuixLogReviewerObjects.NuixLogEntryItemProvider provider)) { return -1; }

            var ids = provider.Ids;
            if (ids == null) { return -1; }

            // AllEntriesIDList maps index -> index+1, so the index of id X is X-1.
            if (ids is LogRepository.AllEntriesIDList)
            {
                long idx = id - 1;
                return (idx >= 0 && idx < ids.Count) ? (int)idx : -1;
            }

            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == id) { return i; }
            }
            return -1;
        }

        /// <summary>
        /// Scrolls so the target row sits roughly in the middle of the viewport (rather than clamped to
        /// the top/bottom edge), by scrolling a page-half past it then back to it. Best-effort: falls
        /// back to a plain ScrollIntoView if the ScrollViewer can't be found.
        /// </summary>
        private void CenterRow(int index, int total)
        {
            var sv = FindScrollViewer(resultsGrid);
            if (sv == null || total <= 0) { return; }

            double viewportRows = sv.ViewportHeight; // in item units under row virtualization
            if (viewportRows <= 0) { return; }

            double target = index - viewportRows / 2.0;
            if (target < 0) { target = 0; }
            sv.ScrollToVerticalOffset(target);
        }

        public event SelectedCellsChangedEventHandler SelectedCellsChanged
        {
            add { resultsGrid.SelectedCellsChanged += value; }
            remove { resultsGrid.SelectedCellsChanged -= value; }
        }

        // Events
        public delegate void SelectedLogEntryChangedDel(NuixLogEntry selectedEntry);
        public event SelectedLogEntryChangedDel SelectedLogEntryChanged;

        /// <summary>Raised when the user chooses to pivot the search around a specific entry's time.</summary>
        public delegate void PivotAroundEntryDel(NuixLogEntry entry);
        public event PivotAroundEntryDel PivotAroundEntryRequested;

        /// <summary>
        /// Raised as the grid scrolls, reporting the event-time span of the rows currently in view
        /// (ticks). Both null when nothing is visible.
        /// </summary>
        public delegate void VisibleRangeChangedDel(long? startTicks, long? endTicks);
        public event VisibleRangeChangedDel VisibleRangeChanged;

        /// <summary>Computes and raises the time span of the currently visible rows.</summary>
        public void ReportVisibleRange()
        {
            if (VisibleRangeChanged == null) return;

            var entries = CurrentLogEntries;
            if (entries == null || entries.Count == 0)
            {
                VisibleRangeChanged(null, null);
                return;
            }

            var sv = FindScrollViewer(resultsGrid);
            double extent = sv?.ExtentHeight ?? 0;
            if (sv == null || extent <= 0)
            {
                VisibleRangeChanged(null, null);
                return;
            }

            // With row virtualization the ScrollViewer's extent/offset/viewport are in item units.
            int total = entries.Count;
            int first = (int)Math.Floor(sv.VerticalOffset / extent * total);
            int last = (int)Math.Ceiling((sv.VerticalOffset + sv.ViewportHeight) / extent * total) - 1;
            if (first < 0) first = 0;
            if (last >= total) last = total - 1;
            if (last < first) last = first;

            long a = entries[first].TimeStamp.Ticks;
            long b = entries[last].TimeStamp.Ticks;
            VisibleRangeChanged(Math.Min(a, b), Math.Max(a, b));
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv) return sv;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private void resultsGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            if (e.AddedCells != null && e.AddedCells.Count > 0)
            {
                var entry = e.AddedCells.First().Item as NuixLogEntry;
                SelectedLogEntryChanged(entry);
            }
        }

        private void menuPivotAroundEvent_Click(object sender, RoutedEventArgs e)
        {
            var entry = SelectedEntry;
            if (entry == null)
            {
                MessageBox.Show("Select a log entry first, then pivot around its event time.");
                return;
            }
            PivotAroundEntryRequested?.Invoke(entry);
        }
    }
}
