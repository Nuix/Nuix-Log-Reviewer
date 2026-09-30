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

        /// <summary>
        /// The id of the entry that best represents the current position, for a history/bookmark
        /// snapshot: the selected row's id if there is a selection, otherwise the top-visible row's id,
        /// or null if the grid is empty. Restored later via <see cref="SelectAndScrollTo"/>.
        /// </summary>
        public long? CurrentPositionEntryId
        {
            get
            {
                var sel = SelectedEntry;
                if (sel != null) { return sel.ID; }

                var entries = CurrentLogEntries;
                if (entries == null || entries.Count == 0) { return null; }

                var sv = FindScrollViewer(resultsGrid);
                double extent = sv?.ExtentHeight ?? 0;
                if (sv == null || extent <= 0) { return null; }

                // Same top-visible index derivation as ReportVisibleRange (offset is in item units
                // under row virtualization).
                int total = entries.Count;
                int first = (int)Math.Floor(sv.VerticalOffset / extent * total);
                if (first < 0) first = 0;
                if (first >= total) first = total - 1;
                var top = entries[first];
                return top?.ID;
            }
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

            // Show/hide the Δ (gap) column live from the shared gap-visual settings. (DataGridColumns
            // aren't in the visual tree, so their Visibility is toggled here rather than by binding.)
            var gaps = GapVisualSettings.Instance;
            colGap.Visibility = gaps.ShowGapColumn ? Visibility.Visible : Visibility.Collapsed;
            gaps.Changed += (s, e) =>
                colGap.Visibility = gaps.ShowGapColumn ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetLogEntries(IList<NuixLogEntry> entries)
        {
            // Drop any selection/current-item BEFORE swapping the source. With a virtualized collection,
            // if a row was selected (e.g. after an operation drill selected the op-start entry), WPF
            // tries to preserve currency across the ItemsSource change by locating the old item in the
            // NEW collection - which, via the default ICollectionView, can walk the list and page every
            // row in from the DB. On a ~1M+ set that's a multi-second UI-thread hang. Clearing selection
            // first removes the item WPF would otherwise hunt for, so the swap stays O(1).
            resultsGrid.SelectedItem = null;
            resultsGrid.SelectedIndex = -1;
            if (resultsGrid.SelectedCells.Count > 0) { resultsGrid.UnselectAllCells(); }

            CurrentLogEntries = entries;
            resultsGrid.ItemsSource = entries;
            // A new result set invalidates any prior Find matches; the caller re-runs the find if wanted.
            _findMatchIds = null;
            colFindMatch.Visibility = Visibility.Collapsed;
            // Report the initial visible window once layout settles.
            Dispatcher.BeginInvoke(new Action(ReportVisibleRange), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// The current result set's time-ordered entry-id list (the same ordinal order as the grid
        /// rows), or null if unavailable. Exposed so the Find engine can stream ids -> rows without
        /// forcing the virtualizing collection to materialize every page as objects.
        /// </summary>
        public IList<long> CurrentOrderedIds
        {
            get
            {
                if (!(CurrentLogEntries is LogRepository.LogEntrySearchResponse response)) { return null; }
                if (!(response.ItemsProvider is NuixLogReviewerObjects.NuixLogEntryItemProvider provider)) { return null; }
                return provider.Ids;
            }
        }

        /// <summary>
        /// Selects the row for the entry with the given id and scrolls it into view. Used by "pivot
        /// around event", history restore, and Find next/prev. No-op if the id isn't in the current set.
        /// </summary>
        /// <remarks>
        /// The results are a data-virtualized collection whose IndexOf/Contains are intentionally
        /// unimplemented (return -1/false). That means the DataGrid CANNOT resolve a programmatic
        /// <c>SelectedItem = item</c> (it calls Items.IndexOf internally, gets -1, and the selection
        /// silently no-ops - which is why the detail view never reacted). So we drive selection through
        /// the row CONTAINER instead: resolve the id's ordinal, scroll that index into view by OFFSET
        /// (CenterRow - also IndexOf-free), let layout realize the container, then set
        /// <see cref="DataGridRow.IsSelected"/> on it. Setting IsSelected on the container updates the
        /// grid's real selection and raises SelectedCellsChanged, which the detail view listens to.
        /// </remarks>
        public void SelectAndScrollTo(long id)
        {
            int index = IndexOfEntryId(id);
            if (index < 0) { return; }

            var entries = CurrentLogEntries;
            if (entries == null || index >= entries.Count) { return; }

            // Scroll the target index into view by OFFSET (no IndexOf needed), then select its container
            // once realized. Deferred so any pending ItemsSource change is applied first.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CenterRow(index, entries.Count);
                resultsGrid.UpdateLayout();
                // After the centering scroll + layout, the container at this index should be realized.
                // Try to select it; if it isn't realized yet (async page fetch), retry once at a lower
                // priority.
                if (!TrySelectContainer(index))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        resultsGrid.UpdateLayout();
                        TrySelectContainer(index);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                ReportVisibleRange();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Selects the realized row container at <paramref name="index"/> (driving the grid's real
        /// selection + SelectedCellsChanged) and focuses it. Returns false if the container isn't
        /// realized yet, so the caller can retry. Works with the data-virtualized collection because it
        /// never relies on Items.IndexOf.
        /// </summary>
        private bool TrySelectContainer(int index)
        {
            if (!(resultsGrid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row))
            {
                return false;
            }
            row.IsSelected = true;      // real selection; raises SelectedCellsChanged -> detail view
            resultsGrid.SelectedItem = row.Item; // keep SelectedItem in sync (item is the realized one)
            row.Focus();
            return true;
        }

        // Lazily-built id -> ordinal-index lookup for the CURRENT result set's id list, so repeated
        // position restores (history Back/Forward, Find next/prev, pivot) don't each linear-scan a huge
        // list. Keyed on the Ids list reference; rebuilt when the result set (hence the list) changes.
        private IList<long> _indexLookupSourceIds;
        private Dictionary<long, int> _indexLookup;

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

            // For a materialized id list (e.g. the time-sorted blank-query GetAllIds, or a drill's id
            // set), build an id -> index map ONCE per result set and reuse it. Avoids an O(n) linear
            // scan on every position restore, which is costly when returning to a ~1M+ set after a drill.
            if (!ReferenceEquals(_indexLookupSourceIds, ids))
            {
                var map = new Dictionary<long, int>(ids.Count);
                for (int i = 0; i < ids.Count; i++) { map[ids[i]] = i; } // first occurrence wins (ids unique)
                _indexLookup = map;
                _indexLookupSourceIds = ids;
            }
            return _indexLookup.TryGetValue(id, out int found) ? found : -1;
        }

        // --- Find within results -------------------------------------------------------------------
        // The set of entry ids matching the current Find term (owned by MainWindow's find engine).
        // Kept here so that rows realized LATER by data-virtualization (on scroll) also get marked, via
        // resultsGrid_LoadingRow. Null/empty => no active find (indicator column hidden).
        private HashSet<long> _findMatchIds;

        /// <summary>
        /// Marks the rows whose ids are in <paramref name="matchIds"/> with the Find indicator dot,
        /// shows the indicator column, and updates any already-realized rows. Rows realized later are
        /// marked on the fly in <see cref="resultsGrid_LoadingRow"/>. Pass null/empty to clear.
        /// </summary>
        public void SetFindMatches(HashSet<long> matchIds)
        {
            _findMatchIds = (matchIds != null && matchIds.Count > 0) ? matchIds : null;
            colFindMatch.Visibility = _findMatchIds != null ? Visibility.Visible : Visibility.Collapsed;
            ApplyFindMatchesToRealizedRows();
        }

        /// <summary>Clears the Find indicator and hides its column.</summary>
        public void ClearFindMatches() => SetFindMatches(null);

        /// <summary>
        /// Applies the current match set to the rows currently realized as containers (data
        /// virtualization means only these exist as objects). Off-screen rows are handled when they
        /// realize, in <see cref="resultsGrid_LoadingRow"/>.
        /// </summary>
        private void ApplyFindMatchesToRealizedRows()
        {
            for (int i = 0; i < resultsGrid.Items.Count; i++)
            {
                // ContainerFromIndex returns null for unrealized (virtualized-away) rows, so this only
                // touches realized ones - it does NOT force materialization of the whole set.
                if (resultsGrid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row
                    && row.Item is NuixLogEntry entry)
                {
                    entry.MatchHighlight = _findMatchIds != null && _findMatchIds.Contains(entry.ID);
                }
            }
        }

        /// <summary>
        /// As each row is realized (including on scroll under data-virtualization), stamp its Find-match
        /// state from the current match set so the indicator dot is correct without materializing the
        /// whole result set.
        /// </summary>
        private void resultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is NuixLogEntry entry)
            {
                entry.MatchHighlight = _findMatchIds != null && _findMatchIds.Contains(entry.ID);
            }
        }

        /// <summary>
        /// Resolves an entry id to its ordinal index in the current set and selects+scrolls to it.
        /// Thin wrapper over <see cref="SelectAndScrollTo"/> used by Find next/prev.
        /// </summary>
        public void SelectAndScrollToId(long id) => SelectAndScrollTo(id);

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

        /// <summary>The kinds of "filter to this row's …" drills offered by the row context menu.</summary>
        public enum RowFilterKind { File, Job, Source, Day, Level, Flag }

        /// <summary>
        /// Raised when the user picks a "Filter to ▸ …" item. <paramref name="arg"/> carries the flag
        /// name for <see cref="RowFilterKind.Flag"/> (ignored otherwise). MainWindow turns this into a
        /// history-aware drill query ANDed onto the current view.
        /// </summary>
        public delegate void RowFilterDel(RowFilterKind kind, NuixLogEntry entry, string arg);
        public event RowFilterDel RowFilterRequested;

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

        /// <summary>
        /// Rebuilds the row context menu for the right-clicked (selected) entry: the static "Pivot" item,
        /// then a "Filter to ▸" submenu whose items reflect what THIS row can be filtered by (job only for
        /// worker rows; a per-flag list only when the row is flagged), then a "Copy ▸" submenu. Built
        /// dynamically so the offered anchors always match the row under the cursor.
        /// </summary>
        private void rowContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Keep the first item (Pivot); rebuild everything after it for the current row.
            for (int i = rowContextMenu.Items.Count - 1; i >= 1; i--) { rowContextMenu.Items.RemoveAt(i); }

            var entry = SelectedEntry;
            if (entry == null) { return; }

            rowContextMenu.Items.Add(new Separator());

            var filterTo = new MenuItem { Header = "Filter to" };

            filterTo.Items.Add(MakeFilterItem("This file", RowFilterKind.File, entry, null));

            // Job: only worker-log rows are attributable to a job.
            string jobId = LogRepository.JobIdExtractor.Extract(entry.FilePath);
            if (!string.IsNullOrEmpty(jobId))
            {
                filterTo.Items.Add(MakeFilterItem("This job (" + jobId + ")", RowFilterKind.Job, entry, null));
            }

            if (!string.IsNullOrWhiteSpace(entry.Source))
            {
                filterTo.Items.Add(MakeFilterItem("This source (" + entry.Source + ")", RowFilterKind.Source, entry, null));
            }

            filterTo.Items.Add(MakeFilterItem("This day (" + entry.TimeStamp.ToString("yyyy-MM-dd") + ")", RowFilterKind.Day, entry, null));

            if (!string.IsNullOrWhiteSpace(entry.Level))
            {
                filterTo.Items.Add(MakeFilterItem("This level (" + entry.Level + ")", RowFilterKind.Level, entry, null));
            }

            // Flags: one item per classifier flag on the row (submenu when several).
            var flags = entry.Flags?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
            if (flags != null && flags.Count > 0)
            {
                if (flags.Count == 1)
                {
                    filterTo.Items.Add(MakeFilterItem("This flag (" + flags[0] + ")", RowFilterKind.Flag, entry, flags[0]));
                }
                else
                {
                    var flagParent = new MenuItem { Header = "This flag" };
                    foreach (var f in flags)
                    {
                        flagParent.Items.Add(MakeFilterItem(f, RowFilterKind.Flag, entry, f));
                    }
                    filterTo.Items.Add(flagParent);
                }
            }

            rowContextMenu.Items.Add(filterTo);

            // Copy submenu.
            rowContextMenu.Items.Add(new Separator());
            var copy = new MenuItem { Header = "Copy" };
            copy.Items.Add(MakeCopyItem("Timestamp", entry.TimeStamp.ToString("yyyy-MM-dd HH:mm:ss.fff")));
            if (!string.IsNullOrWhiteSpace(entry.Source)) { copy.Items.Add(MakeCopyItem("Source", entry.Source)); }
            copy.Items.Add(MakeCopyItem("Content", entry.Content ?? ""));
            copy.Items.Add(MakeCopyItem("Full line", entry.ToLogLine()));
            rowContextMenu.Items.Add(copy);
        }

        /// <summary>Builds a "Filter to" leaf that raises RowFilterRequested for the given kind/arg.</summary>
        private MenuItem MakeFilterItem(string header, RowFilterKind kind, NuixLogEntry entry, string arg)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => RowFilterRequested?.Invoke(kind, entry, arg);
            return mi;
        }

        /// <summary>Builds a "Copy" leaf that puts the given text on the clipboard.</summary>
        private MenuItem MakeCopyItem(string header, string text)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) =>
            {
                try { System.Windows.Clipboard.SetText(text ?? ""); } catch { /* clipboard contention; ignore */ }
            };
            return mi;
        }
    }
}
