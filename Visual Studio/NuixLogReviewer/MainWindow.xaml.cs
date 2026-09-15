using NuixLogReviewer.GUI;
using NuixLogReviewer.LogRepository;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

namespace NuixLogReviewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private string appDir = null;
        private NuixLogRepo repo = null;

        private bool isBusy = false;
        /// <summary>
        /// When set to true, throws up a "Please Wait..." control over the whole window which:
        /// - Blocks user input to other controls
        /// - Allows a status to be shown
        /// - Allows a progress value to be shown
        /// Setting this to false hides the "Please Wait..." control again.
        /// </summary>
        public bool IsBusy
        {
            get { return isBusy; }
            set
            {
                isBusy = value;

                if (value == true) { busyOverlay.Visibility = Visibility.Visible; }
                else { busyOverlay.Visibility = Visibility.Collapsed; }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            // Determine where the EXE lives
            appDir = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            // Tell NuixLogRepo where it can create its temp resources
            NuixLogRepo.RepoRootDirectory = System.IO.Path.Combine(appDir, "TempRepos");
            repo = new NuixLogRepo();

            levelChart.TimeRangeSelected += levelChart_TimeRangeSelected;

            rebuildSavedSearchesMenu();
        }

        /// <summary>
        /// Rebuilds the menu items in the saved searches menu by asking for a fresh listing of them
        /// from SavedSearchesRepo class.
        /// </summary>
        private void rebuildSavedSearchesMenu()
        {
            menuAppendSearch.Items.Clear();
            menuReplaceSearch.Items.Clear();
            foreach (var savedSearch in SavedSearchesRepo.GetSavedSearches())
            {
                MenuItem appender = new MenuItem();
                appender.Header = savedSearch.Name;
                appender.ToolTip = "Query: " + savedSearch.Query;
                appender.Icon = new System.Windows.Controls.Image
                {
                    Source = new BitmapImage(new Uri("zoom.png", UriKind.Relative))
                };
                appender.Click += (z, x) =>
                {
                    string currentQuery = txtSearchQuery.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(currentQuery))
                    {
                        txtSearchQuery.Text = currentQuery + " AND " + savedSearch.Query;
                    }
                    else
                    {
                        txtSearchQuery.Text = savedSearch.Query;
                    }

                };
                menuAppendSearch.Items.Add(appender);

                MenuItem replacer = new MenuItem();
                replacer.Header = savedSearch.Name;
                replacer.ToolTip = "Query: " + savedSearch.Query;
                replacer.Icon = new System.Windows.Controls.Image
                {
                    Source = new BitmapImage(new Uri("zoom.png", UriKind.Relative))
                };
                replacer.Click += (z, x) =>
                {
                    txtSearchQuery.Text = savedSearch.Query;
                };
                menuReplaceSearch.Items.Add(replacer);
            }
        }

        /// <summary>
        /// Updates the StatusBar "Range:" label with the given event-time span (typically that of
        /// the current filtered result set).
        /// </summary>
        private void updateDateRangeDisplay(DateTime? min, DateTime? max)
        {
            if (min.HasValue && max.HasValue)
            {
                TimeSpan span = max.Value - min.Value;
                string spanText = FormatSpan(span);
                lblDateRange.Content = string.Format("{0:yyyy-MM-dd HH:mm:ss} \u2192 {1:yyyy-MM-dd HH:mm:ss}  ({2})",
                    min.Value, max.Value, spanText);
            }
            else
            {
                lblDateRange.Content = "\u2014";
            }
        }

        /// <summary>
        /// Formats a TimeSpan compactly, e.g. "5d 16h", "3h 12m", "45s".
        /// </summary>
        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{span.Hours}h {span.Minutes}m";
            if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
            return $"{span.Seconds}s";
        }

        /// <summary>
        /// When the user drags a region on the time chart, overwrite the search with a timestamp
        /// range spanning the selected window and re-run. The user can refine from there.
        /// </summary>
        private void levelChart_TimeRangeSelected(long minTicks, long maxTicks)
        {
            txtSearchQuery.Text = string.Format("timestamp:[{0} TO {1}]", minTicks, maxTicks);
            performSearch();
        }

        /// <summary>
        /// When a user selects a given row in the log entry grid, display that entry's details
        /// in the leg entry viewer.
        /// </summary>
        /// <param name="selectedEntry">The log entry which was selected.</param>
        private void resultsGrid_SelectedLogEntryChanged(NuixLogEntry selectedEntry)
        {
            logEntryViewer.SetLogEntry(selectedEntry);
            levelChart.SetSelectionMarker(selectedEntry != null ? selectedEntry.TimeStamp.Ticks : (long?)null);
            if (selectedEntry != null)
            {
                bottomTabs.SelectedItem = tabDetails;
            }
        }

        /// <summary>Reflects the grid's currently visible rows as a shaded band on the chart.</summary>
        private void resultsGrid_VisibleRangeChanged(long? startTicks, long? endTicks)
        {
            levelChart.SetVisibleRange(startTicks, endTicks);
        }

        /// <summary>
        /// Handles the grid's "Pivot Around Event Time" request: prompts for a +/- window, then
        /// overwrites the search with a timestamp range spanning that window around the entry's
        /// event time and runs it. The user can then refine from there.
        /// </summary>
        private void resultsGrid_PivotAroundEntryRequested(NuixLogEntry entry)
        {
            if (entry == null) { return; }

            var dialog = new PivotTimeDialog(entry.TimeStamp) { Owner = this };
            dialog.ShowDialog();
            if (!dialog.Success) { return; }

            long startTicks = (entry.TimeStamp - dialog.Window).Ticks;
            long endTicks = (entry.TimeStamp + dialog.Window).Ticks;
            if (startTicks < 0) { startTicks = 0; }

            // Overwrite the query with the pivot range (ticks), then run it. Ask the search to select
            // and scroll to the entry we pivoted on so it stays in view within the loaded context.
            txtSearchQuery.Text = string.Format("timestamp:[{0} TO {1}]", startTicks, endTicks);
            performSearch(entry.ID);
        }

        /// <summary>
        /// Allows a user to select one or more individual log files to be loaded using loadLogFiles method.
        /// </summary>
        private void menuLoadNuixLogs_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog();
            ofd.Title = "Load Nuix Logs";
            ofd.Filter = "Nuix Logs|nuix*.log*;*.log";
            ofd.Multiselect = true;
            ofd.InitialDirectory = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LocalAppData"), @"Nuix\Logs");
            if (ofd.ShowDialog() == true)
            {
                // Dispose of current repo
                NuixLogRepo prev = repo;
                repo = new NuixLogRepo();
                prev.DisposeRepo();

                string[] filesToLoad = ofd.FileNames;
                loadLogFiles(filesToLoad);
            }
        }

        /// <summary>
        /// Allows a user to select a directory. All structured Nuix/Automate log files under it are
        /// located (see <see cref="FindStructuredLogFiles"/>) and loaded via loadLogFiles. Unstructured
        /// blob logs (stdout/stderr/derby) are excluded.
        /// </summary>
        private void menuLoadDirectory_Click(object sender, RoutedEventArgs e)
        {
            VistaFolderBrowserDialog dialog = new VistaFolderBrowserDialog();
            if (dialog.ShowDialog() == true)
            {
                string selectedDirectory = dialog.SelectedPath;
                string[] logFiles = FindStructuredLogFiles(selectedDirectory);

                // Dispose of current repo
                NuixLogRepo prev = repo;
                repo = new NuixLogRepo();
                prev.DisposeRepo();

                loadLogFiles(logFiles);
            }
        }

        // Basenames of unstructured "blob" logs (whole-file output rather than per-line records).
        // These are deliberately excluded from directory loading since the reviewer only handles
        // logs where each entry begins with a timestamped record header.
        private static readonly HashSet<string> ExcludedBlobLogNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "stdout.log",
            "stderr.log",
            "derby.log",
        };

        // Filename patterns for the structured Nuix / Automate logs the reviewer can parse. Matched
        // case-insensitively against each file's name. "*.log*" trailing wildcard also picks up
        // rolled files like "nuix.log.1".
        private static readonly string[] StructuredLogPatterns = new[]
        {
            "nuix*.log*",                   // classic Workstation logs (incl. worker nuix.log)
            "automate-scheduler*.log*",     // Automate scheduler
            "automate-engine-server*.log*", // Automate engine server
            "engine.*-job.*.log*",          // Automate engine per-job logs
            "engine.*-init.log*",           // Automate engine init logs
        };

        /// <summary>
        /// Recursively finds structured Nuix/Automate log files under the given directory, excluding
        /// unstructured blob logs (stdout/stderr/derby). A file is included if its name matches any
        /// of <see cref="StructuredLogPatterns"/> and is not in <see cref="ExcludedBlobLogNames"/>.
        /// </summary>
        private static string[] FindStructuredLogFiles(string directory)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in StructuredLogPatterns)
            {
                foreach (var path in System.IO.Directory.EnumerateFiles(directory, pattern, System.IO.SearchOption.AllDirectories))
                {
                    if (ExcludedBlobLogNames.Contains(System.IO.Path.GetFileName(path)))
                    {
                        continue;
                    }
                    results.Add(path);
                }
            }
            return results.ToArray();
        }

        /// <summary>
        /// Loads a series of log files into the repo for searching and review.
        /// </summary>
        /// <param name="filesToLoad">Array of absolute log file paths.</param>
        private void loadLogFiles(string[] filesToLoad)
        {
            if (filesToLoad.Length > 0)
            {
                lblStatus.Text = "Loading log files...";
                IsBusy = true;
                Task loadFilesTask = new Task(() =>
                {
                    ProgressBroadcaster pb = new ProgressBroadcaster();
                    pb.StatusUpdated += (statusMessage) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            lblStatus.Text = statusMessage;
                        }));
                    };
                    pb.ProgressUpdated += (progress) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            lblProgress.Text = progress.ToString("###,###,##0");
                        }));
                    };

                    repo.LoadLogFiles(filesToLoad, pb);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtSearchQuery.Text = "";
                        // The classifier table fills from performSearch() below (per-filtered-set counts).
                        performSearch();
                    }));
                });
                loadFilesTask.Start();
            }
        }

        /// <summary>
        /// If the user hits enter on search bar, run the search.
        /// </summary>
        private void txtSearchQuery_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                performSearch();
            }
        }

        /// <summary>
        /// Performs whatever search is in the search bar.
        /// </summary>
        /// <param name="selectEntryId">
        /// When provided, after results load the grid selects and scrolls to the entry with this id
        /// (used by "pivot around event" so the pivoted-on entry stays in view). Null selects nothing.
        /// </param>
        private void performSearch(long? selectEntryId = null)
        {
            IsBusy = true;
            string query = txtSearchQuery.Text;
            logEntryViewer.Clear();
            levelChart.ShowPlaceholder("Charting...");
            lblStatus.Text = "Executing search:\n" + query;
            lblProgress.Text = "";

            Task searchTask = new Task(() =>
            {
                if (repo.Database.TotalRecords < 1)
                {
                    MessageBox.Show("Please load some log files first.");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsBusy = false;
                    }));
                    return;
                }

                try
                {
                    LogEntrySearchResponse hits = repo.Search(query);
                    // Time-series for the chart (single-pass, fast). ~120 buckets gives a smooth
                    // full-width strip; the chart downsamples visually as needed.
                    var series = repo.GetTimeSeries(query, 120);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lblRecordCounts.Content = String.Format("{0} / {1}", hits.Count.ToString("###,###,##0"), repo.Database.TotalRecords.ToString("###,###,##0"));

                        lblInfoCount.Content = hits.InfoEntryCount.ToString("###,###,##0");
                        lblInfoCount.IsEnabled = hits.InfoEntryCount > 0;

                        lblWarnCount.Content = hits.WarnEntryCount.ToString("###,###,##0");
                        lblWarnCount.IsEnabled = hits.WarnEntryCount > 0;

                        lblErrorCount.Content = hits.ErrorEntryCount.ToString("###,###,##0");
                        lblErrorCount.IsEnabled = hits.ErrorEntryCount > 0;

                        lblDebugCount.Content = hits.DebugEntryCount.ToString("###,###,##0");
                        lblDebugCount.IsEnabled = hits.DebugEntryCount > 0;

                        updateDateRangeDisplay(hits.FilteredMinTime, hits.FilteredMaxTime);

                        levelChart.SetData(series);

                        // Populate the classifier table with per-flag counts for the current set,
                        // most frequent first. Flags with a zero count in this set are omitted.
                        flagList.ItemsSource = hits.FlagCounts
                            .Where(kv => kv.Value > 0)
                            .OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key)
                            .Select(kv => new LogRepository.FlagCount { Name = kv.Key, Count = kv.Value })
                            .ToList();

                        resultsGrid.SetLogEntries(hits);

                        // After a pivot, keep the entry we pivoted on selected and in view within the
                        // freshly loaded context window.
                        if (selectEntryId.HasValue)
                        {
                            resultsGrid.SelectAndScrollTo(selectEntryId.Value);
                        }
                    }));
                }
                catch (Exception exc)
                {
                    MessageBox.Show(exc.Message);
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsBusy = false;
                    }));
                }
            });
            searchTask.Start();
        }

        /// <summary>
        /// Clears the query in the search bar then runs an all items search (blank query).
        /// </summary>
        private void btnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            txtSearchQuery.Clear();
            performSearch();
        }

        /// <summary>
        /// Button to run whatever search is in the search bar.
        /// </summary>
        private void btnRunSearch_Click(object sender, RoutedEventArgs e)
        {
            performSearch();
        }

        /// <summary>
        /// When the window is closing we will try to have the repo cleanup its on disk work product:
        /// - SQLite DB
        /// - Lucene search index
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            repo.DisposeRepo();
        }

        /// <summary>
        /// When user double clicks a classifier row, drill the search down to that flag.
        /// </summary>
        private void flagList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (flagList.SelectedItem is LogRepository.FlagCount fc && !string.IsNullOrEmpty(fc.Name))
            {
                drillDownSearch("flag:" + fc.Name, false);
            }
        }

        /// <summary>The most recently computed patterns, retained so the sort toggle can re-order them.</summary>
        private IList<LogSearchIndex.LogPattern> _lastPatterns;

        /// <summary>
        /// Mines the current result set into message-template patterns and shows them in the Patterns
        /// tab. Runs off the UI thread (like search) with the busy overlay, since the full-set pass can
        /// take a few hundred milliseconds.
        /// </summary>
        private void btnComputePatterns_Click(object sender, RoutedEventArgs e)
        {
            if (repo.Database.TotalRecords < 1)
            {
                MessageBox.Show("Please load some log files first.");
                return;
            }

            IsBusy = true;
            lblStatus.Text = "Computing patterns...";
            lblProgress.Text = "";
            string query = txtSearchQuery.Text;

            Task.Run(() =>
            {
                try
                {
                    var patterns = repo.GetPatterns(query);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _lastPatterns = patterns;
                        applyPatternSort();
                        bottomTabs.SelectedItem = tabPatterns;
                        IsBusy = false;
                    }));
                }
                catch (Exception exc)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsBusy = false;
                        MessageBox.Show(exc.Message);
                    }));
                }
            });
        }

        /// <summary>
        /// Current pattern sort column ("Count" | "First Seen" | "Last Seen" | "Pattern") and
        /// direction. Defaults to Count (with direction driven by the "Rare first" toggle). Header
        /// clicks update these; the "Rare first" checkbox is kept in sync when Count is the sort key.
        /// </summary>
        private string _patternSortHeader = "Count";
        private bool _patternSortAscending = true; // matches "Rare first" (checked) default => count ascending

        /// <summary>Re-orders the last computed patterns per the active sort column/direction and binds them.</summary>
        private void applyPatternSort()
        {
            if (_lastPatterns == null) return;

            IEnumerable<LogSearchIndex.LogPattern> ordered;
            switch (_patternSortHeader)
            {
                case "First Seen":
                    ordered = _patternSortAscending
                        ? _lastPatterns.OrderBy(p => p.FirstSeen)
                        : _lastPatterns.OrderByDescending(p => p.FirstSeen);
                    break;
                case "Last Seen":
                    ordered = _patternSortAscending
                        ? _lastPatterns.OrderBy(p => p.LastSeen)
                        : _lastPatterns.OrderByDescending(p => p.LastSeen);
                    break;
                case "Pattern":
                    ordered = _patternSortAscending
                        ? _lastPatterns.OrderBy(p => p.Template, StringComparer.OrdinalIgnoreCase)
                        : _lastPatterns.OrderByDescending(p => p.Template, StringComparer.OrdinalIgnoreCase);
                    break;
                case "Count":
                default:
                    ordered = _patternSortAscending
                        ? _lastPatterns.OrderBy(p => p.Count)
                        : _lastPatterns.OrderByDescending(p => p.Count);
                    break;
            }

            patternList.ItemsSource = ordered.ToList();
            SyncPatternSortArrow();
        }

        /// <summary>
        /// Ensures the sort-direction arrow sits on the active sort column header with the right
        /// direction, and is cleared from all others. Driven from applyPatternSort so it stays correct
        /// no matter what triggered the sort (a header click, the "Rare first" checkbox, or the initial
        /// Compute Patterns). Each header's Tag ("asc"/"desc"/null) is turned into an up/down triangle
        /// by the SortableHeaderStyle template. Safe to call before the headers exist (no-op then).
        /// </summary>
        private void SyncPatternSortArrow()
        {
            foreach (var h in FindVisualChildren<System.Windows.Controls.GridViewColumnHeader>(patternList))
            {
                if (!(h.Content is string text) || string.IsNullOrEmpty(text))
                {
                    continue; // skip the trailing padding header
                }
                h.Tag = (text == _patternSortHeader)
                    ? (_patternSortAscending ? "asc" : "desc")
                    : null;
            }
        }

        /// <summary>Depth-first enumeration of visual descendants of a given type.</summary>
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// The "Rare first" toggle is just a shortcut for sorting by Count ascending (rare) vs
        /// descending (frequent). Keep it wired to the same sort model as the column headers.
        /// </summary>
        private void chkRareFirst_Click(object sender, RoutedEventArgs e)
        {
            _patternSortHeader = "Count";
            _patternSortAscending = (chkRareFirst.IsChecked == true);
            applyPatternSort();
        }

        /// <summary>
        /// Sorts the patterns table when a column header is clicked. Clicking the current sort column
        /// flips the direction; clicking a different column sorts it (Count/First/Last default to
        /// descending as the most useful first look; Pattern defaults to ascending A-Z). The "Rare
        /// first" checkbox is kept in sync when Count is the active sort column, and applyPatternSort
        /// moves the direction arrow to the active header.
        /// </summary>
        private void patternList_ColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is System.Windows.Controls.GridViewColumnHeader header)) return;
            // Clicking the padding header (no content) or the column separators yields no usable header.
            if (!(header.Content is string headerText) || string.IsNullOrEmpty(headerText)) return;

            if (_patternSortHeader == headerText)
            {
                _patternSortAscending = !_patternSortAscending;
            }
            else
            {
                _patternSortHeader = headerText;
                // Pattern reads best A-Z; the numeric/time columns read best largest/newest first.
                _patternSortAscending = (headerText == "Pattern");
            }

            // Keep the "Rare first" checkbox meaningful: it reflects Count-ascending, and is only
            // relevant when Count is the sort key.
            if (_patternSortHeader == "Count")
            {
                chkRareFirst.IsChecked = _patternSortAscending;
            }

            applyPatternSort();
        }

        /// <summary>Copy is available whenever at least one pattern row is selected.</summary>
        private void patternCopy_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = patternList.SelectedItems.Count > 0;
        }

        /// <summary>Ctrl+C / context-menu "Copy": copies the selected rows as tab-separated text (with a header row).</summary>
        private void patternCopyRows_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            CopySelectedPatterns(templatesOnly: false);
        }

        private void patternCopyRows_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedPatterns(templatesOnly: false);
        }

        /// <summary>Context-menu "Copy Pattern(s) Only": copies just the template text, one per line.</summary>
        private void patternCopyTemplates_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedPatterns(templatesOnly: true);
        }

        /// <summary>
        /// Copies the selected pattern rows to the clipboard in the order they appear in the grid.
        /// Full mode emits a header plus tab-separated Count/First Seen/Last Seen/Pattern (paste-friendly
        /// into a spreadsheet or a chat); templates-only emits just the pattern text, one per line, which
        /// is what's most useful for refining the masker's collapse rules.
        /// </summary>
        private void CopySelectedPatterns(bool templatesOnly)
        {
            // SelectedItems is in selection order; project onto the grid's current display order so the
            // copied text matches what the user sees.
            var selected = new HashSet<LogSearchIndex.LogPattern>(
                patternList.SelectedItems.OfType<LogSearchIndex.LogPattern>());
            if (selected.Count == 0) { return; }

            var rows = patternList.Items.OfType<LogSearchIndex.LogPattern>()
                .Where(selected.Contains)
                .ToList();

            var sb = new StringBuilder();
            if (templatesOnly)
            {
                foreach (var p in rows)
                {
                    sb.AppendLine(p.Template);
                }
            }
            else
            {
                sb.AppendLine("Count\tFirst Seen\tLast Seen\tPattern");
                foreach (var p in rows)
                {
                    sb.AppendLine(string.Format("{0}\t{1:yyyy-MM-dd HH:mm:ss}\t{2:yyyy-MM-dd HH:mm:ss}\t{3}",
                        p.Count, p.FirstSeen, p.LastSeen, p.Template));
                }
            }

            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception)
            {
                // The clipboard can transiently be locked by another process; ignore rather than crash.
            }
        }
        /// </summary>
        private void patternList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(patternList.SelectedItem is LogSearchIndex.LogPattern pattern) || pattern.Ids == null)
            {
                return;
            }

            var ids = pattern.Ids as IList<long> ?? pattern.Ids.ToList();
            var hits = repo.BuildResponseForIds(ids);

            lblRecordCounts.Content = String.Format("{0} / {1}",
                hits.Count.ToString("###,###,##0"), repo.Database.TotalRecords.ToString("###,###,##0"));
            lblInfoCount.Content = hits.InfoEntryCount.ToString("###,###,##0");
            lblInfoCount.IsEnabled = hits.InfoEntryCount > 0;
            lblWarnCount.Content = hits.WarnEntryCount.ToString("###,###,##0");
            lblWarnCount.IsEnabled = hits.WarnEntryCount > 0;
            lblErrorCount.Content = hits.ErrorEntryCount.ToString("###,###,##0");
            lblErrorCount.IsEnabled = hits.ErrorEntryCount > 0;
            lblDebugCount.Content = hits.DebugEntryCount.ToString("###,###,##0");
            lblDebugCount.IsEnabled = hits.DebugEntryCount > 0;

            updateDateRangeDisplay(hits.FilteredMinTime, hits.FilteredMaxTime);

            // Reflect the drilled set on the chart with its real level distribution over its own span.
            var series = repo.GetTimeSeriesForIds(ids, 120);
            levelChart.SetData(series);

            resultsGrid.SetLogEntries(hits);
        }

        /// <summary>
        /// Takes the query in the search bar (if there is one) and augments it with additional criteria
        /// and then runs the new serach.
        /// </summary>
        /// <param name="additionalCriteria">Additional criteria to add to the query.  Will be ANDed to value in search bar if there is one.</param>
        private void drillDownSearch(string additionalCriteria, bool executeNewQuery = true)
        {
            string existingQuery = txtSearchQuery.Text;
            if (String.IsNullOrWhiteSpace(existingQuery))
            {
                txtSearchQuery.Text = additionalCriteria;
                if (executeNewQuery) { performSearch(); }
            }
            else
            {
                txtSearchQuery.Text = String.Format("({0}) AND {1}", existingQuery, additionalCriteria);
                if (executeNewQuery) { performSearch(); }
            }
        }

        /// <summary>
        /// Augments current search bar query with "level:info" and runs new search.
        /// </summary>
        private void lblInfoCount_Click(object sender, RoutedEventArgs e)
        {
            drillDownSearch("level:info");
        }

        /// <summary>
        /// Augments current search bar query with "level:warn" and runs new search.
        /// </summary>
        private void lblWarnCount_Click(object sender, RoutedEventArgs e)
        {
            drillDownSearch("level:warn");
        }

        /// <summary>
        /// Augments current search bar query with "level:error" and runs new search.
        /// </summary>
        private void lblErrorCount_Click(object sender, RoutedEventArgs e)
        {
            drillDownSearch("level:error");
        }

        /// <summary>
        /// Augments current search bar query with "level:debug" and runs new search.
        /// </summary>
        private void lblDebugCount_Click(object sender, RoutedEventArgs e)
        {
            drillDownSearch("level:debug");
        }

        private void menuExportCurrentEntries_Click(object sender, RoutedEventArgs e)
        {
            IList<NuixLogEntry> currentEntries = resultsGrid.CurrentLogEntries;
            if (currentEntries == null || currentEntries.Count < 1)
            {
                MessageBox.Show("There are no log entries to export!");
                return;
            }

            Microsoft.Win32.SaveFileDialog sfd = new Microsoft.Win32.SaveFileDialog();
            sfd.Title = "Select Output Log File";
            // TODO: Might be a more useful default suggested file name?
            sfd.FileName = "NuixLogSubset.log";
            sfd.Filter = "Log File (*.log)|*.log";
            if (sfd.ShowDialog() == true)
            {
                IsBusy = true;

                ProgressBroadcaster pb = new ProgressBroadcaster();
                pb.StatusUpdated += (statusMessage) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lblStatus.Text = statusMessage;
                    }));
                };
                pb.ProgressUpdated += (progress) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lblProgress.Text = progress.ToString("###,###,##0");
                    }));
                };

                pb.BroadcastStatus("Exporting log entries to: " + sfd.FileName);

                Task exportTask = new Task(() =>
                {
                    using (System.IO.StreamWriter sw = new System.IO.StreamWriter(sfd.FileName))
                    {
                        for (int i = 0; i < currentEntries.Count; i++)
                        {
                            pb.BroadcastProgress(i + 1);
                            NuixLogEntry entry = currentEntries[i];
                            sw.WriteLine(entry.ToLogLine());
                        }
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsBusy = false;
                    }));
                });

                exportTask.Start();
            }
        }

        private void menuSaveCurrentSearch_Click(object sender, RoutedEventArgs e)
        {
            string query = txtSearchQuery.Text.Trim();

            if (string.IsNullOrWhiteSpace(query))
            {
                string title = "Query is Empty";
                string message = "Current query is empty.  Not much point in saving that right?";
                MessageBox.Show(message, title);
                return;
            }
            else
            {
                SaveSearchDialog ssd = new SaveSearchDialog();
                ssd.Owner = this;
                ssd.ShowDialog();
                if (ssd.Success)
                {
                    string name = ssd.ProvidedName;

                    SavedSearchesRepo.SaveSearch(name, query);
                    rebuildSavedSearchesMenu();
                }
            }
        }
    }
}
