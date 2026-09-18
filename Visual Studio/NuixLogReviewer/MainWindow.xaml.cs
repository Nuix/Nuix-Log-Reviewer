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
            levelChart.TimeClicked += levelChart_TimeClicked;
            levelChart.ResolutionChanged += levelChart_ResolutionChanged;

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
        /// Single-clicking the timeline scrolls the grid to the nearest log event at or before the
        /// clicked time ("round backwards"), selecting it. Does not change the current search - it's a
        /// navigation aid within the shown set. Scoped to the query in the search bar, which is what
        /// the chart reflects for a normal search.
        /// </summary>
        private void levelChart_TimeClicked(long ticks)
        {
            if (repo.Database.TotalRecords < 1) { return; }

            // Use the SAME effective query the grid is showing (base query AND NOT hidden classifiers),
            // so the closest-event id we find is actually present in the current view and can be
            // selected. Using the bare search text could return a hidden entry that isn't in the grid.
            string effectiveQuery = buildEffectiveQuery(txtSearchQuery.Text);
            long? id = repo.FindEntryIdAtOrBefore(effectiveQuery, ticks);
            if (id.HasValue)
            {
                resultsGrid.SelectAndScrollTo(id.Value);
            }
        }

        /// <summary>
        /// How to (re-)fetch the currently-charted time series at a given bucket count. Set by whatever
        /// last populated the chart: the search path (effective query) or an id-set drill-down. Lets a
        /// chart resize re-bucket the same data at a finer/coarser resolution. Null before any chart.
        /// </summary>
        private Func<int, LogSearchIndex.TimeSeries> _chartSource;

        /// <summary>
        /// Re-fetches the current chart source at the chart's new desired resolution (off the UI thread,
        /// like the other chart fetches) and updates the chart. Triggered by the chart when a resize
        /// changes how many time-slices it wants.
        /// </summary>
        private void levelChart_ResolutionChanged(int buckets)
        {
            var source = _chartSource;
            if (source == null || repo == null || repo.Database.TotalRecords < 1) { return; }

            Task.Run(() =>
            {
                LogSearchIndex.TimeSeries series;
                try { series = source(buckets); }
                catch { return; } // transient (e.g. repo reset mid-resize); ignore, next fetch recovers
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    levelChart.UpdateResolution(series);
                    levelChart.MarkRequested(buckets);
                }));
            });
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
                        // Seed the session hidden-set from the configured defaults for the flags present
                        // in this loaded set. Done once per load; the user can then toggle per session.
                        seedVisibilityDefaults();
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
            string baseQuery = txtSearchQuery.Text;

            // History: unless this search IS a history restore, first stamp where we currently are onto
            // the current history entry (so Back returns to this scroll position), then remember the
            // destination to push once results land. Captured here at the top because the grid still
            // holds the OUTGOING results (it's repopulated later in the dispatcher callback).
            if (!_restoringHistory)
            {
                _history.UpdateCurrentPosition(captureViewState());
            }

            // Hidden classifiers are a view filter: they're ANDed onto the running query (NOT flag:...),
            // but never written into the search box. Classifier counts + the "excluded" figure are
            // computed on the BASE query so hidden classifiers still show their real counts and can be
            // un-hidden.
            string effectiveQuery = buildEffectiveQuery(baseQuery);

            logEntryViewer.Clear();
            levelChart.ShowPlaceholder("Charting...");
            lblStatus.Text = "Executing search:\n" + baseQuery;
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
                    // Effective (hidden-filtered) set drives the grid, level readouts, range and chart.
                    LogEntrySearchResponse hits = repo.Search(effectiveQuery);
                    // Chart resolution scales with the chart's rendered width (see LevelTimeChart);
                    // remember how to re-fetch this series so a resize can re-bucket it.
                    int chartBuckets = levelChart.CurrentDesiredBucketCount;
                    string chartQuery = effectiveQuery;
                    _chartSource = n => repo.GetTimeSeries(chartQuery, n);
                    var series = repo.GetTimeSeries(effectiveQuery, chartBuckets);

                    // Base set (NOT hidden-filtered) drives the classifier table and the excluded count,
                    // so hidden classifiers still report their true counts.
                    var baseSummary = repo.Summarize(baseQuery);
                    int excluded = Math.Max(0, baseSummary.Total - hits.Count);

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
                        levelChart.MarkRequested(chartBuckets);

                        // Classifier table from the BASE set (unfiltered by visibility). Each row's eye
                        // reflects the current session hidden-set. Zero-count flags are omitted.
                        flagList.ItemsSource = baseSummary.FlagCounts
                            .Where(kv => kv.Value > 0)
                            .OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key)
                            .Select(kv => new LogRepository.FlagCount
                            {
                                Name = kv.Key,
                                Count = kv.Value,
                                IsShown = !_hiddenFlags.Contains(kv.Key),
                                Description = LogRepository.Classifiers.ClassifierDescriptionRegistry.DescriptionFor(kv.Key),
                            })
                            .ToList();

                        updateExcludedIndicator(excluded);

                        // Files table from the BASE set (unfiltered by visibility), like classifiers.
                        // Short display names are the shortest-unique path tails across the matched files;
                        // the full path is the tooltip and the drill-down/hide key.
                        var fileShortNames = LogRepository.FileDisplay.ShortUniqueNames(
                            baseSummary.FileCounts.Where(kv => kv.Value > 0).Select(kv => kv.Key));
                        fileList.ItemsSource = baseSummary.FileCounts
                            .Where(kv => kv.Value > 0)
                            .OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(kv => new LogRepository.FileEntry
                            {
                                Path = kv.Key,
                                DisplayName = fileShortNames.TryGetValue(kv.Key, out var dn) ? dn : kv.Key,
                                Count = kv.Value,
                                IsShown = !_hiddenFiles.Contains(kv.Key),
                            })
                            .ToList();

                        resultsGrid.SetLogEntries(hits);

                        // After a pivot, keep the entry we pivoted on selected and in view within the
                        // freshly loaded context window.
                        if (selectEntryId.HasValue)
                        {
                            resultsGrid.SelectAndScrollTo(selectEntryId.Value);
                        }

                        // History: record this destination view (unless we're restoring one). The
                        // position anchor is the pivot target if any, else whatever the grid lands on;
                        // Push dedups same-view re-runs. Then refresh the Back/Forward enabled state.
                        if (!_restoringHistory)
                        {
                            _history.Push(new ViewState(
                                baseQuery,
                                _hiddenFlags, _hiddenFiles, _hiddenTemplates,
                                selectEntryId ?? resultsGrid.CurrentPositionEntryId));
                        }
                        updateHistoryButtons();
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
        /// Opens the Classifier Defaults editor. The dialog lists the flags in the current data (plus
        /// any already in the config) with a "shown by default" checkbox, and saves to
        /// ClassifierVisibility.config. Saved defaults apply on the next load (per-session toggles in the
        /// Classifiers tab are unaffected).
        /// </summary>
        private void menuClassifierDefaults_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<string> loadedFlags = (repo.Database.TotalRecords > 0)
                ? repo.Database.GetAllFlags()
                : Enumerable.Empty<string>();

            var dlg = new ClassifierDefaultsDialog(loadedFlags) { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>
        /// Opens Windows Explorer at the dedicated configuration folder (see <see cref="ConfigPaths"/>),
        /// which holds only the user-editable config (ClassifierScripts\, LogPatterns.config,
        /// ClassifierVisibility.config, SavedSearches\) - not the exe/DLLs.
        /// </summary>
        private void menuOpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = ConfigPaths.Root; // created on access if missing
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    MessageBox.Show("Could not locate the configuration folder.");
                    return;
                }

                // Launch Explorer at the folder. UseShellExecute so the OS resolves explorer.exe.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the configuration folder:\n" + ex.Message);
            }
        }

        /// <summary>
        /// Session set of classifier flags currently hidden from the view. Seeded from
        /// ClassifierVisibility.config defaults on each load, then toggled by the eye buttons.
        /// Case-insensitive on flag name.
        /// </summary>
        private readonly HashSet<string> _hiddenFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resets the session hidden-set and reseeds it from ClassifierVisibility.config for the flags
        /// present in the freshly loaded set. Called once per load, so a new load starts from the saved
        /// defaults again (session toggles don't leak across loads).
        /// </summary>
        private void seedVisibilityDefaults()
        {
            _hiddenFlags.Clear();
            var defaults = ClassifierVisibilityRepo.Load(); // flag -> hiddenByDefault
            foreach (var flag in repo.Database.GetAllFlags())
            {
                if (defaults.TryGetValue(flag, out bool hidden) && hidden)
                {
                    _hiddenFlags.Add(flag);
                }
            }
        }

        /// <summary>
        /// Builds the "hide these classifiers" clause: NOT (flag:a OR flag:b ...) for the currently
        /// hidden flags, or "" when nothing is hidden.
        /// </summary>
        private string buildHiddenFilter()
        {
            if (_hiddenFlags.Count == 0) return "";
            string ors = string.Join(" OR ", _hiddenFlags.Select(f => "flag:" + f));
            return "NOT (" + ors + ")";
        }

        /// <summary>
        /// Session set of source files (full paths, lower-cased) currently hidden from the view.
        /// Toggled by the Files tab eye buttons; not persisted across loads.
        /// </summary>
        private readonly HashSet<string> _hiddenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The "hide these files" clause: NOT (file:"a" OR file:"b" ...), or "" when none.</summary>
        private string buildHiddenFileFilter()
        {
            return LogRepository.FileDisplay.HideClause(_hiddenFiles);
        }

        /// <summary>
        /// Session set of masked message templates currently hidden from the view (Patterns tab eye
        /// toggles). Lower-cased comparison to match the indexed tmpl key. Not persisted across loads.
        /// </summary>
        private readonly HashSet<string> _hiddenTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The "hide these patterns" clause: NOT (tmpl:"a" OR tmpl:"b" ...), or "" when none.</summary>
        private string buildHiddenTemplateFilter()
        {
            return LogRepository.PatternQuery.HideClause(_hiddenTemplates);
        }

        /// <summary>
        /// The effective query the grid/chart/counts actually run: the user's query ANDed with the
        /// hidden-classifier, hidden-file, and hidden-pattern view filters. All are view-only (never
        /// written to the search box). Used by the search path and the timeline click lookup.
        /// </summary>
        private string buildEffectiveQuery(string baseQuery)
        {
            string q = composeEffectiveQuery(baseQuery, buildHiddenFilter());
            q = composeEffectiveQuery(q, buildHiddenFileFilter());
            q = composeEffectiveQuery(q, buildHiddenTemplateFilter());
            return q;
        }

        /// <summary>Combines a query with a NOT(...) hide clause. Handles blank query / blank clause.</summary>
        private static string composeEffectiveQuery(string baseQuery, string hiddenFilter)
        {
            if (string.IsNullOrWhiteSpace(hiddenFilter)) return baseQuery;
            if (string.IsNullOrWhiteSpace(baseQuery)) return hiddenFilter;
            return string.Format("({0}) AND {1}", baseQuery, hiddenFilter);
        }

        // ===================== Navigation history (Back/Forward) =====================

        private readonly NavigationHistory _history = new NavigationHistory();
        /// <summary>True while a Back/Forward restore is running, so performSearch doesn't re-push.</summary>
        private bool _restoringHistory;

        /// <summary>Snapshots the current view (query + session hidden-sets + grid position).</summary>
        private ViewState captureViewState()
        {
            return new ViewState(
                txtSearchQuery.Text,
                _hiddenFlags, _hiddenFiles, _hiddenTemplates,
                resultsGrid.CurrentPositionEntryId);
        }

        /// <summary>
        /// Applies a <see cref="ViewState"/>: restores the query box and the session hidden-sets, re-runs
        /// the search (guarded so it doesn't push a new history entry), and scrolls to the saved position.
        /// Shared by Back and Forward.
        /// </summary>
        private void restoreViewState(ViewState vs)
        {
            if (vs == null) return;

            _restoringHistory = true;
            try
            {
                txtSearchQuery.Text = vs.BaseQuery ?? "";
                _hiddenFlags.Clear(); foreach (var f in vs.HiddenFlags) _hiddenFlags.Add(f);
                _hiddenFiles.Clear(); foreach (var f in vs.HiddenFiles) _hiddenFiles.Add(f);
                _hiddenTemplates.Clear(); foreach (var t in vs.HiddenTemplates) _hiddenTemplates.Add(t);

                performSearch(vs.PositionEntryId);
            }
            finally
            {
                _restoringHistory = false;
            }
            updateHistoryButtons();
        }

        /// <summary>Navigates back one step in view history.</summary>
        private void btnHistoryBack_Click(object sender, RoutedEventArgs e)
        {
            // Stamp current position first so returning forward lands where we are now.
            _history.UpdateCurrentPosition(captureViewState());
            var vs = _history.Back();
            if (vs != null) { restoreViewState(vs); }
        }

        /// <summary>Navigates forward one step in view history.</summary>
        private void btnHistoryForward_Click(object sender, RoutedEventArgs e)
        {
            _history.UpdateCurrentPosition(captureViewState());
            var vs = _history.Forward();
            if (vs != null) { restoreViewState(vs); }
        }

        /// <summary>Enables/disables the Back/Forward buttons per the history pointer.</summary>
        private void updateHistoryButtons()
        {
            if (btnHistoryBack != null) { btnHistoryBack.IsEnabled = _history.CanBack; }
            if (btnHistoryForward != null) { btnHistoryForward.IsEnabled = _history.CanForward; }
        }

        /// <summary>Alt+Left / Alt+Right drive Back / Forward view history (browser-style).</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // With Alt held, the actual key arrives via SystemKey.
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Left) { btnHistoryBack_Click(this, null); e.Handled = true; }
                else if (key == Key.Right) { btnHistoryForward_Click(this, null); e.Handled = true; }
            }
        }

        /// <summary>Updates the status-bar indicator for how many rows the hidden classifiers removed.</summary>
        private void updateExcludedIndicator(int excluded)
        {
            if (excluded > 0)
            {
                lblExcluded.Content = string.Format("{0:N0} hidden by classifiers", excluded);
                lblExcluded.Visibility = Visibility.Visible;
                sepExcluded.Visibility = Visibility.Visible;
            }
            else
            {
                lblExcluded.Visibility = Visibility.Collapsed;
                sepExcluded.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Toggles a classifier row's shown/hidden state (the eye button), updates the session
        /// hidden-set, and re-runs the search so the view reflects the change immediately.
        /// </summary>
        private void flagEyeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is LogRepository.FlagCount fc) || string.IsNullOrEmpty(fc.Name))
            {
                return;
            }

            fc.IsShown = !fc.IsShown;
            if (fc.IsShown) { _hiddenFlags.Remove(fc.Name); }
            else { _hiddenFlags.Add(fc.Name); }

            performSearch();
        }

        /// <summary>
        /// Hides every currently-listed classifier from the view (adds them all to the session
        /// hidden-set) and re-runs the search. With everything hidden, the view shows only entries
        /// that carry no classifier flag.
        /// </summary>
        private void btnHideAllFlags_Click(object sender, RoutedEventArgs e)
        {
            if (!(flagList.ItemsSource is IEnumerable<LogRepository.FlagCount> rows)) { return; }

            bool changed = false;
            foreach (var fc in rows)
            {
                if (string.IsNullOrEmpty(fc.Name)) { continue; }
                if (fc.IsShown) { fc.IsShown = false; changed = true; }
                _hiddenFlags.Add(fc.Name);
            }

            if (changed) { performSearch(); }
        }

        /// <summary>
        /// Shows every classifier again by clearing the entire session hidden-set (a full reset, so it
        /// also un-hides any flag not currently listed), then re-runs the search.
        /// </summary>
        private void btnShowAllFlags_Click(object sender, RoutedEventArgs e)
        {
            bool changed = _hiddenFlags.Count > 0;
            _hiddenFlags.Clear();

            if (flagList.ItemsSource is IEnumerable<LogRepository.FlagCount> rows)
            {
                foreach (var fc in rows)
                {
                    if (!fc.IsShown) { fc.IsShown = true; }
                }
            }

            if (changed) { performSearch(); }
        }

        /// <summary>
        /// Toggles a file row's shown/hidden state (the eye button), updates the session hidden-file set,
        /// and re-runs the search so the view reflects the change immediately.
        /// </summary>
        private void fileEyeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is LogRepository.FileEntry fe) || string.IsNullOrEmpty(fe.Path))
            {
                return;
            }

            fe.IsShown = !fe.IsShown;
            if (fe.IsShown) { _hiddenFiles.Remove(fe.Path); }
            else { _hiddenFiles.Add(fe.Path); }

            performSearch();
        }

        /// <summary>Hides every currently-listed file from the view and re-runs the search.</summary>
        private void btnHideAllFiles_Click(object sender, RoutedEventArgs e)
        {
            if (!(fileList.ItemsSource is IEnumerable<LogRepository.FileEntry> rows)) { return; }

            bool changed = false;
            foreach (var fe in rows)
            {
                if (string.IsNullOrEmpty(fe.Path)) { continue; }
                if (fe.IsShown) { fe.IsShown = false; changed = true; }
                _hiddenFiles.Add(fe.Path);
            }

            if (changed) { performSearch(); }
        }

        /// <summary>Shows every file again by clearing the entire session hidden-file set, then re-runs.</summary>
        private void btnShowAllFiles_Click(object sender, RoutedEventArgs e)
        {
            bool changed = _hiddenFiles.Count > 0;
            _hiddenFiles.Clear();

            if (fileList.ItemsSource is IEnumerable<LogRepository.FileEntry> rows)
            {
                foreach (var fe in rows)
                {
                    if (!fe.IsShown) { fe.IsShown = true; }
                }
            }

            if (changed) { performSearch(); }
        }

        /// <summary>
        /// Double-clicking a file drills the view down to just that file's entries via a real
        /// file:"&lt;path&gt;" query (replacing the search box), so the level chips, classifier toggles
        /// and chart compose on top of it - same approach as the Jobs double-click.
        /// </summary>
        private void fileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(fileList.SelectedItem is LogRepository.FileEntry fe) || string.IsNullOrEmpty(fe.Path))
            {
                return;
            }

            txtSearchQuery.Text = LogRepository.FileDisplay.DrillDownQuery(fe.Path);
            performSearch();
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
        /// Toggles a pattern's shown/hidden state. Updates the session hidden-template set and re-runs
        /// the main search so the grid/chart reflect it immediately - but does NOT recompute the
        /// Patterns list (it's a snapshot); the row just flips its eye, and a stale hint appears since
        /// the listed counts no longer match the filtered view. Recompute refreshes them.
        /// </summary>
        private void patternEyeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is LogSearchIndex.LogPattern p) || p.Template == null)
            {
                return;
            }

            string key = p.Template.ToLowerInvariant();
            p.IsShown = !p.IsShown;
            if (p.IsShown) { _hiddenTemplates.Remove(key); }
            else { _hiddenTemplates.Add(key); }

            setPatternsStale(true);   // listed counts are now as-of last Compute, not the live view
            performSearch();          // grid/chart/counts update live via buildEffectiveQuery
        }

        /// <summary>
        /// Shows every pattern again: clears the session hidden-template set, flips the listed rows'
        /// eyes back on, and re-runs the search. Leaves the snapshot counts as-is (marks stale).
        /// </summary>
        private void btnShowAllPatterns_Click(object sender, RoutedEventArgs e)
        {
            bool changed = _hiddenTemplates.Count > 0;
            _hiddenTemplates.Clear();
            if (_lastPatterns != null)
            {
                foreach (var p in _lastPatterns) { if (!p.IsShown) p.IsShown = true; }
            }
            if (changed) { setPatternsStale(true); performSearch(); }
        }

        /// <summary>
        /// Hides every currently-listed pattern from the view (adds all listed templates to the session
        /// hidden set) and re-runs the search. The Patterns list stays a snapshot (rows grey, counts
        /// as-computed); marks stale.
        /// </summary>
        private void btnHideAllPatterns_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPatterns == null) { return; }

            bool changed = false;
            foreach (var p in _lastPatterns)
            {
                if (string.IsNullOrEmpty(p.Template)) { continue; }
                if (p.IsShown) { p.IsShown = false; changed = true; }
                _hiddenTemplates.Add(p.Template.ToLowerInvariant());
            }
            if (changed) { setPatternsStale(true); performSearch(); }
        }

        /// <summary>Shows/hides the "counts are from the last Compute" stale hint on the Patterns tab.</summary>
        private void setPatternsStale(bool stale)
        {
            lblPatternsStale.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        }

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
                        // Seed each pattern's eye state from the session hidden-set. Compute mines the
                        // BASE query, so a hidden pattern still appears here (marked eye-off) - which is
                        // what lets the user un-hide it after a recompute. Recompute clears "stale".
                        foreach (var p in patterns)
                        {
                            p.IsShown = !_hiddenTemplates.Contains((p.Template ?? "").ToLowerInvariant());
                        }
                        _lastPatterns = patterns;
                        setPatternsStale(false);
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
                case "Level":
                    // Sort by dominant-level severity (ERROR>WARN>INFO>DEBUG>none), then by count.
                    ordered = _patternSortAscending
                        ? _lastPatterns.OrderBy(p => LevelSeverityRank(p.DominantLevel)).ThenBy(p => p.Count)
                        : _lastPatterns.OrderByDescending(p => LevelSeverityRank(p.DominantLevel)).ThenByDescending(p => p.Count);
                    break;
                case "ERR":
                    ordered = _patternSortAscending ? _lastPatterns.OrderBy(p => p.Error) : _lastPatterns.OrderByDescending(p => p.Error);
                    break;
                case "WARN":
                    ordered = _patternSortAscending ? _lastPatterns.OrderBy(p => p.Warn) : _lastPatterns.OrderByDescending(p => p.Warn);
                    break;
                case "INFO":
                    ordered = _patternSortAscending ? _lastPatterns.OrderBy(p => p.Info) : _lastPatterns.OrderByDescending(p => p.Info);
                    break;
                case "DBG":
                    ordered = _patternSortAscending ? _lastPatterns.OrderBy(p => p.Debug) : _lastPatterns.OrderByDescending(p => p.Debug);
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

        /// <summary>Severity rank for dominant-level sorting: ERROR highest, empty lowest.</summary>
        private static int LevelSeverityRank(string level)
        {
            switch (level)
            {
                case "ERROR": return 4;
                case "WARN": return 3;
                case "INFO": return 2;
                case "DEBUG": return 1;
                default: return 0;
            }
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
                sb.AppendLine("Count\tLevel\tERR\tWARN\tINFO\tDBG\tFirst Seen\tLast Seen\tPattern");
                foreach (var p in rows)
                {
                    sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6:yyyy-MM-dd HH:mm:ss}\t{7:yyyy-MM-dd HH:mm:ss}\t{8}",
                        p.Count, p.DominantLevel, p.Error, p.Warn, p.Info, p.Debug, p.FirstSeen, p.LastSeen, p.Template));
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
            showIdsInGrid(ids);
        }

        /// <summary>
        /// Populates the grid with exactly the entries for the given ids (an exact drill-down), and
        /// updates the level readouts, range label and chart to match. Shared by the Patterns and Jobs
        /// drill-downs.
        /// </summary>
        private void showIdsInGrid(IList<long> ids)
        {
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
            // Resolution scales with the chart width; remember the id-set so a resize can re-bucket it.
            int chartBuckets = levelChart.CurrentDesiredBucketCount;
            var chartIds = ids;
            _chartSource = n => repo.GetTimeSeriesForIds(chartIds, n);
            var series = repo.GetTimeSeriesForIds(ids, chartBuckets);
            levelChart.SetData(series);
            levelChart.MarkRequested(chartBuckets);

            resultsGrid.SetLogEntries(hits);
        }

        // ===================== Jobs tab =====================

        /// <summary>The most recently computed jobs, retained so the sort and the chart overlay can reuse them.</summary>
        private IList<LogSearchIndex.JobInfo> _lastJobs;
        private string _jobSortHeader = "Start";
        private bool _jobSortAscending = true;

        /// <summary>
        /// Groups the current result set into worker jobs and shows them in the Jobs tab. Runs off the
        /// UI thread (like search / patterns) since the single-pass grouping can take a moment on a
        /// large set. When "Show on timeline" is checked, the computed spans are also overlaid on the chart.
        /// </summary>
        private void btnComputeJobs_Click(object sender, RoutedEventArgs e)
        {
            if (repo.Database.TotalRecords < 1)
            {
                MessageBox.Show("Please load some log files first.");
                return;
            }

            IsBusy = true;
            lblStatus.Text = "Computing jobs...";
            lblProgress.Text = "";
            string query = txtSearchQuery.Text;

            Task.Run(() =>
            {
                try
                {
                    var jobs = repo.GetJobs(query);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _lastJobs = jobs;
                        applyJobSort();
                        bottomTabs.SelectedItem = tabJobs;
                        updateJobChartOverlay();
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

        /// <summary>Re-orders the last computed jobs per the active sort column/direction and binds them.</summary>
        private void applyJobSort()
        {
            if (_lastJobs == null) return;

            IEnumerable<LogSearchIndex.JobInfo> ordered;
            switch (_jobSortHeader)
            {
                case "Job":
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.JobId, StringComparer.OrdinalIgnoreCase)
                                                : _lastJobs.OrderByDescending(j => j.JobId, StringComparer.OrdinalIgnoreCase);
                    break;
                case "End":
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.LastSeen) : _lastJobs.OrderByDescending(j => j.LastSeen);
                    break;
                case "Duration":
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.Duration) : _lastJobs.OrderByDescending(j => j.Duration);
                    break;
                case "Entries":
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.EntryCount) : _lastJobs.OrderByDescending(j => j.EntryCount);
                    break;
                case "Warns":
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.WarnCount) : _lastJobs.OrderByDescending(j => j.WarnCount);
                    break;
                case "Errors":
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.ErrorCount) : _lastJobs.OrderByDescending(j => j.ErrorCount);
                    break;
                case "Start":
                default:
                    ordered = _jobSortAscending ? _lastJobs.OrderBy(j => j.FirstSeen) : _lastJobs.OrderByDescending(j => j.FirstSeen);
                    break;
            }

            jobList.ItemsSource = ordered.ToList();
            SyncJobSortArrow();
        }

        private void SyncJobSortArrow()
        {
            foreach (var h in FindVisualChildren<System.Windows.Controls.GridViewColumnHeader>(jobList))
            {
                if (!(h.Content is string text) || string.IsNullOrEmpty(text)) continue;
                h.Tag = (text == _jobSortHeader) ? (_jobSortAscending ? "asc" : "desc") : null;
            }
        }

        /// <summary>
        /// Sorts the Jobs grid on header click. Same behavior as the Patterns grid: clicking the active
        /// column flips direction; Job sorts A-Z first, the time/numeric columns descending first.
        /// </summary>
        private void jobList_ColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is System.Windows.Controls.GridViewColumnHeader header)) return;
            if (!(header.Content is string headerText) || string.IsNullOrEmpty(headerText)) return;

            if (_jobSortHeader == headerText)
            {
                _jobSortAscending = !_jobSortAscending;
            }
            else
            {
                _jobSortHeader = headerText;
                _jobSortAscending = (headerText == "Job" || headerText == "Start");
            }
            applyJobSort();
        }

        /// <summary>
        /// Double-clicking a job drills the grid down to exactly that job's entries. We do this as a
        /// real query (<c>job:job-&lt;id&gt;</c>) rather than pushing the raw id list into the grid, so
        /// the search box reflects the drill-down and the level chips / classifier toggles / chart all
        /// compose on top of it (e.g. clicking ERROR then runs <c>(job:...) AND level:error</c>).
        /// The indexed "job" field holds exactly this JobId, so the query matches the same entries.
        /// </summary>
        private void jobList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(jobList.SelectedItem is LogSearchIndex.JobInfo job) || string.IsNullOrEmpty(job.JobId))
            {
                return;
            }

            // Drill down from the current query, ANDing in this job. Replace rather than append the
            // search box so the double-click is a clean "show me this job" action.
            txtSearchQuery.Text = "job:" + job.JobId;
            performSearch();
        }

        private void chkShowJobsOnChart_Click(object sender, RoutedEventArgs e)
        {
            updateJobChartOverlay();
        }

        /// <summary>Highlights the selected job's bracket on the timeline (or clears it when none selected).</summary>
        private void jobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var jobId = (jobList.SelectedItem as LogSearchIndex.JobInfo)?.JobId;
            levelChart.SetSelectedJob(jobId);
        }

        /// <summary>Pushes (or clears) the computed job spans onto the timeline chart overlay.</summary>
        private void updateJobChartOverlay()
        {
            if (chkShowJobsOnChart.IsChecked == true && _lastJobs != null)
            {
                levelChart.SetJobSpans(_lastJobs);
            }
            else
            {
                levelChart.SetJobSpans(null);
            }
        }


        /// <summary>
        /// Takes the query in the search bar (if there is one) and augments it with additional criteria
        /// and then runs the new search.
        /// </summary>
        /// <param name="additionalCriteria">Additional criteria to add to the query.  Will be ANDed to value in search bar if there is one.</param>
        private void drillDownSearch(string additionalCriteria, bool executeNewQuery = true)
        {
            txtSearchQuery.Text = ComposeAnd(txtSearchQuery.Text, additionalCriteria);
            if (executeNewQuery) { performSearch(); }
        }

        /// <summary>
        /// ANDs <paramref name="addition"/> onto <paramref name="existing"/> without piling up
        /// redundant parentheses. Rules:
        /// <list type="bullet">
        /// <item>Blank existing → just the addition.</item>
        /// <item>Existing already wrapped in one pair of parens that spans the whole thing → reused as
        /// is (no second layer).</item>
        /// <item>Existing has a top-level (unparenthesized) OR → wrapped once, because AND binds tighter
        /// and we must preserve the OR grouping.</item>
        /// <item>Otherwise (a single term or a pure AND chain) → the addition is simply appended with
        /// AND, no parens needed.</item>
        /// </list>
        /// The additions we generate here are always single atoms (level:x, flag:x, job:x), so they
        /// never need wrapping themselves.
        /// </summary>
        internal static string ComposeAnd(string existing, string addition)
        {
            if (string.IsNullOrWhiteSpace(existing)) { return addition; }
            existing = existing.Trim();

            // If the whole thing is already a single balanced parenthesized group, extend inside the
            // AND at the top level rather than adding another wrapper: "(a OR b)" + c => "(a OR b) AND c".
            // If it's a bare top-level OR, we must wrap to keep the OR grouped under the new AND.
            string toCombine = (!IsFullyWrapped(existing) && HasTopLevelOr(existing))
                ? "(" + existing + ")"
                : existing;

            return toCombine + " AND " + addition;
        }

        /// <summary>
        /// True if the string is a single parenthesized group covering the entire expression, e.g.
        /// "(a OR b)" but not "(a) OR (b)" (whose first '(' closes before the end).
        /// </summary>
        private static bool IsFullyWrapped(string s)
        {
            if (s.Length < 2 || s[0] != '(') { return false; }
            int depth = 0;
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (inQuote) { continue; }
                if (c == '(') { depth++; }
                else if (c == ')')
                {
                    depth--;
                    // If we return to depth 0 before the last char, the leading '(' didn't wrap it all.
                    if (depth == 0 && i < s.Length - 1) { return false; }
                }
            }
            return depth == 0;
        }

        /// <summary>
        /// True if the expression contains an OR / || operator at the top level (paren depth 0, outside
        /// quotes). Such expressions must be wrapped before ANDing so the OR stays grouped.
        /// </summary>
        private static bool HasTopLevelOr(string s)
        {
            int depth = 0;
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (inQuote) { continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth--; continue; }
                if (depth != 0) { continue; }

                // "||" operator.
                if (c == '|' && i + 1 < s.Length && s[i + 1] == '|') { return true; }

                // Whole-word "OR" (case-insensitive), bounded by non-letters so we don't match e.g.
                // a field/value that merely contains the letters "or".
                if ((c == 'O' || c == 'o') && i + 1 < s.Length && (s[i + 1] == 'R' || s[i + 1] == 'r'))
                {
                    bool leftBoundary = (i == 0) || !char.IsLetterOrDigit(s[i - 1]);
                    bool rightBoundary = (i + 2 >= s.Length) || !char.IsLetterOrDigit(s[i + 2]);
                    if (leftBoundary && rightBoundary) { return true; }
                }
            }
            return false;
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
