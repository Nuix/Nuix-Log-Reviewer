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
                else
                {
                    busyOverlay.Visibility = Visibility.Collapsed;
                    // The cancel affordance is per-operation; always hide it when the overlay closes.
                    btnCancelBusy.Visibility = Visibility.Collapsed;
                }
            }
        }

        // ===================== Cancellable search =====================

        /// <summary>Cancellation source for the in-flight search, if any. Replaced on each new search.</summary>
        private System.Threading.CancellationTokenSource _searchCts;

        /// <summary>
        /// Monotonic search id. Incremented per search so a late-finishing (or cancelled) search can tell
        /// it's stale and skip applying its results, even if cancellation didn't halt it in time.
        /// </summary>
        private int _searchGeneration;

        /// <summary>Shows the busy overlay for a cancellable operation (reveals the Cancel button).</summary>
        private void BeginCancellableBusy(string status)
        {
            lblStatus.Text = status;
            lblProgress.Text = "";
            btnCancelBusy.Visibility = Visibility.Visible;
            btnCancelBusy.IsEnabled = true;
            IsBusy = true;
        }

        /// <summary>Cancel button on the busy overlay: signal cancellation and unblock the UI at once.</summary>
        private void btnCancelBusy_Click(object sender, RoutedEventArgs e)
        {
            // Signal the running query to stop (cooperative), bump the generation so its results are
            // discarded even if it finishes anyway, and hide the overlay immediately so the user is not
            // stuck. The background task's finally-block is a no-op for a stale generation.
            _searchGeneration++;
            try { _searchCts?.Cancel(); } catch { /* already disposed */ }
            lblStatus.Text = "Cancelling…";
            btnCancelBusy.IsEnabled = false;
            IsBusy = false;
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
        /// Clicking the "Range:" value copies the displayed range and span (e.g.
        /// "2026-09-18 12:06:31 -> 2026-09-18 14:51:39  (2h 45m)") to the clipboard. No-op when no
        /// range is shown (placeholder dash).
        /// </summary>
        private void lblDateRange_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string text = lblDateRange.Content as string;
            if (string.IsNullOrEmpty(text) || text == "\u2014") return;
            try
            {
                System.Windows.Clipboard.SetText(text);
                lblStatus.Text = "Range copied to clipboard.";
            }
            catch
            {
                // Clipboard can transiently fail if another process holds it; ignore.
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

            // Use the SAME effective view the grid is showing so the closest-event id we find is
            // actually present in the current view and can be selected. Hidden classifiers/files stay in
            // the query string (few clauses); hidden PATTERNS go through the fast set-membership filter
            // (buildEffectiveQueryNoTemplates + the hidden-template list) - NOT an OR'd phrase negation,
            // which made this click re-parse a giant query and hang the UI.
            string effectiveQuery = buildEffectiveQueryNoTemplates(txtSearchQuery.Text);
            var hiddenTemplates = _hiddenTemplates.Count > 0 ? new List<string>(_hiddenTemplates) : null;

            // Run off the UI thread: the lookup can be non-trivial on large sets, and freezing the UI on
            // a single click is exactly the hang we're avoiding. Marshal the scroll back to the UI.
            IsBusy = true;
            Task.Run(() =>
            {
                long? id = null;
                try { id = repo.FindEntryIdAtOrBefore(effectiveQuery, hiddenTemplates, ticks); }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsBusy = false;
                        if (id.HasValue) { resultsGrid.SelectAndScrollTo(id.Value); }
                    }));
                }
            });
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
            "derby-server*.log*",           // Derby network server logs (structured; "HH:mm:ss,fff")
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
                        // A new load replaces the corpus: clear all session/view state carried over from
                        // any previous load in this session (compute-on-demand Patterns/Jobs/Insights
                        // snapshots, hidden-file/template sets, history) so nothing stale lingers.
                        resetSessionState();

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
        /// Resets per-session, per-corpus UI state when a new log set is loaded into the same session.
        /// The grid/chart/classifier/file tables are refreshed by the performSearch() that follows, so
        /// this focuses on the state that would otherwise linger: the compute-on-demand Patterns/Jobs/
        /// Insights snapshots, the hidden-file/template view-filters (hidden-flags are re-seeded
        /// separately), and the navigation history.
        /// </summary>
        private void resetSessionState()
        {
            // Patterns snapshot + its list/labels.
            _lastPatterns = null;
            patternList.ItemsSource = null;
            lblPatternsTotal.Text = "";
            setPatternsStale(false);

            // Jobs snapshot + list.
            _lastJobs = null;
            jobList.ItemsSource = null;

            // Insights list + label.
            insightList.ItemsSource = null;
            lblInsightsInfo.Text = "";

            // Session hidden-sets that aren't otherwise reset (hidden-flags are re-seeded by
            // seedVisibilityDefaults for the new load; files/templates have no re-seed, so clear them).
            _hiddenFiles.Clear();
            _hiddenTemplates.Clear();

            // Navigation history belongs to the previous corpus.
            _history.Clear();
            updateHistoryButtons();

            // Any active Find belongs to the previous corpus.
            clearFind();
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
            // Set up cancellation for this search: cancel any prior in-flight search, start a fresh token
            // and bump the generation so a stale search's results are discarded on arrival.
            try { _searchCts?.Cancel(); } catch { }
            var cts = new System.Threading.CancellationTokenSource();
            _searchCts = cts;
            int generation = ++_searchGeneration;
            var cancelToken = cts.Token;

            string baseQuery = txtSearchQuery.Text;

            // History: unless this search IS a history restore, first stamp where we currently are onto
            // the current history entry (so Back returns to this scroll position), then remember the
            // destination to push once results land. Captured here at the top because the grid still
            // holds the OUTGOING results (it's repopulated later in the dispatcher callback).
            if (!_restoringHistory)
            {
                _history.UpdateCurrentPosition(captureViewState());
            }

            // Hidden classifiers/files are a view filter: they're ANDed onto the running query
            // (NOT flag:.../NOT file:...), but never written into the search box. Hidden PATTERNS are
            // applied separately as a fast set-membership filter (see below) rather than folded into the
            // query string, because hundreds of OR'd template phrases made the parser pathologically
            // slow. effectiveQuery therefore carries only the (few) flag/file clauses.
            string effectiveQuery = buildEffectiveQueryNoTemplates(baseQuery);
            // Snapshot the hidden-template set for the background thread (copy so UI edits can't race).
            var hiddenTemplates = _hiddenTemplates.Count > 0
                ? new List<string>(_hiddenTemplates)
                : null;

            logEntryViewer.Clear();
            levelChart.ShowPlaceholder("Charting...");
            BeginCancellableBusy("Executing search:\n" + baseQuery);

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
                    // Base set (NOT hidden-filtered) drives the classifier table and the excluded count,
                    // so hidden classifiers still report their true counts. Computed first so it can be
                    // REUSED as the effective-set summary when nothing is hidden (the common case),
                    // saving a whole-index scan - see the reuse arg below.
                    cancelToken.ThrowIfCancellationRequested();
                    var baseSummary = repo.Summarize(baseQuery, cancelToken);

                    // When NO view-filters are active (no hidden flags/files/templates) the effective
                    // query IS the base query, so the grid's summary equals baseSummary; reuse it to
                    // skip a duplicate whole-index pass.
                    bool sameAsBase = hiddenTemplates == null
                        && string.Equals(effectiveQuery, baseQuery, StringComparison.Ordinal);

                    // Effective (hidden-filtered) set drives the grid, level readouts, range and chart.
                    // Hidden patterns go through the fast FieldCacheTermsFilter path.
                    cancelToken.ThrowIfCancellationRequested();
                    LogEntrySearchResponse hits = repo.SearchWithHiddenTemplates(
                        effectiveQuery, hiddenTemplates, sameAsBase ? baseSummary : null, cancelToken);
                    // Chart resolution scales with the chart's rendered width (see LevelTimeChart);
                    // remember how to re-fetch this series so a resize can re-bucket it.
                    int chartBuckets = levelChart.CurrentDesiredBucketCount;
                    string chartQuery = effectiveQuery;
                    var chartHidden = hiddenTemplates;
                    _chartSource = n => repo.GetTimeSeriesWithHiddenTemplates(chartQuery, chartHidden, n);
                    cancelToken.ThrowIfCancellationRequested();
                    var series = repo.GetTimeSeriesWithHiddenTemplates(effectiveQuery, hiddenTemplates, chartBuckets, cancelToken);

                    int excluded = Math.Max(0, baseSummary.Total - hits.Count);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Discard results from a search that has since been superseded or cancelled.
                        if (generation != _searchGeneration) { return; }
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
                catch (OperationCanceledException)
                {
                    // Search was cancelled (Cancel button or superseded). The UI was already unblocked
                    // by the cancel handler; nothing to apply.
                }
                catch (Exception exc)
                {
                    // Only surface errors for the still-current search; stale failures are irrelevant.
                    if (generation == _searchGeneration)
                    {
                        Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(exc.Message)));
                    }
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Only the current search clears the overlay - a stale/cancelled search must not
                        // hide the overlay of a newer search that's already running.
                        if (generation == _searchGeneration) { IsBusy = false; }
                    }));
                    if (ReferenceEquals(_searchCts, cts)) { _searchCts = null; }
                    cts.Dispose();
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

        /// <summary>
        /// The effective query WITHOUT the hidden-pattern clause (only the few hidden classifier/file
        /// clauses). The main search path pairs this with <see cref="_hiddenTemplates"/> passed as a
        /// separate set-membership FILTER (FieldCacheTermsFilter), because folding hundreds of pattern
        /// templates into a giant <c>NOT (tmpl:"a" OR ...)</c> string made the classic QueryParser +
        /// boolean rewrite pathologically slow (seconds), paid on every grid/summary/chart pass.
        /// </summary>
        private string buildEffectiveQueryNoTemplates(string baseQuery)
        {
            string q = composeEffectiveQuery(baseQuery, buildHiddenFilter());
            q = composeEffectiveQuery(q, buildHiddenFileFilter());
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

        /// <summary>
        /// Updates the status-bar indicator for how many rows the active view-filters removed. The count
        /// is base-set total minus the effective (filtered) hit count, so it spans ALL view-filters:
        /// hidden classifiers, hidden files, and hidden patterns - hence "hidden by filters", not just
        /// classifiers.
        /// </summary>
        private void updateExcludedIndicator(int excluded)
        {
            if (excluded > 0)
            {
                lblExcluded.Content = string.Format("{0:N0} hidden by filters", excluded);
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

        // ===================== Insights tab =====================

        /// <summary>
        /// Scans the whole loaded corpus for notable findings ("threads to pull on") off the UI thread,
        /// then lists them grouped by their source detector. Whole-set analysis, so it's independent of
        /// the current search/hidden view.
        /// </summary>
        private void btnAnalyzeInsights_Click(object sender, RoutedEventArgs e)
        {
            if (repo.Database.TotalRecords < 1)
            {
                MessageBox.Show("Please load some log files first.");
                return;
            }

            IsBusy = true;
            lblStatus.Text = "Analyzing the loaded logs for insights...";
            lblProgress.Text = "";

            Task.Run(() =>
            {
                try
                {
                    var findings = repo.BuildInsights();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // Group by source in the ListView (matches the grouped GroupStyle in XAML).
                        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(findings);
                        view.GroupDescriptions.Clear();
                        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Source"));
                        insightList.ItemsSource = findings;

                        int actionable = findings.Count(f => f.HasQuery && f.QueryValid);
                        lblInsightsInfo.Text = findings.Count == 0
                            ? "No notable findings."
                            : string.Format("{0} finding{1} · {2} with a jump-to query",
                                findings.Count, findings.Count == 1 ? "" : "s", actionable);
                        bottomTabs.SelectedItem = tabInsights;
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
        /// Double-clicking a finding "pulls the thread": runs its query (honing the view onto the
        /// evidence) and, if it carries a position, scrolls there. Findings with an invalid query show a
        /// message instead of running it (fail-soft, mainly for future scripted detectors).
        /// </summary>
        private void insightList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(insightList.SelectedItem is LogRepository.Insights.Insight insight)) { return; }

            if (insight.HasQuery && !insight.QueryValid)
            {
                MessageBox.Show("This finding's query could not be parsed:\n\n" + (insight.QueryError ?? "unknown error"),
                    "Invalid query", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (insight.HasQuery)
            {
                // Set the search box to the finding's query and run it through the normal search path
                // (which applies view-filters, history, cancellation, etc.).
                txtSearchQuery.Text = insight.Query;
                performSearch(insight.PositionEntryId);
            }
            else if (insight.PositionEntryId.HasValue)
            {
                resultsGrid.SelectAndScrollTo(insight.PositionEntryId.Value);
            }
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
        /// The indexed regex-templates a pattern covers, lower-cased to match the tmpl index key. With
        /// Drain folding a single pattern (whose <see cref="LogSearchIndex.LogPattern.Template"/> may be
        /// a collapsed "&lt;*&gt;" template) represents several regex-templates, so hide/show must apply
        /// to ALL of them. Falls back to the pattern's own template when unfolded.
        /// </summary>
        private static IEnumerable<string> PatternHideKeys(LogSearchIndex.LogPattern p)
        {
            var members = p?.MemberTemplates;
            if (members != null && members.Count > 0)
            {
                foreach (var m in members)
                    if (!string.IsNullOrEmpty(m)) yield return m.ToLowerInvariant();
            }
            else if (!string.IsNullOrEmpty(p?.Template))
            {
                yield return p.Template.ToLowerInvariant();
            }
        }

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

            p.IsShown = !p.IsShown;
            foreach (var key in PatternHideKeys(p))
            {
                if (p.IsShown) { _hiddenTemplates.Remove(key); }
                else { _hiddenTemplates.Add(key); }
            }

            setPatternsStale(true);   // listed counts are now as-of last Compute, not the live view
            performSearch();          // grid/chart/counts update live via the hidden-template filter
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
                foreach (var key in PatternHideKeys(p)) { _hiddenTemplates.Add(key); }
            }
            if (changed) { setPatternsStale(true); performSearch(); }
        }

        /// <summary>Shows/hides the "counts are from the last Compute" stale hint on the Patterns tab.</summary>
        private void setPatternsStale(bool stale)
        {
            lblPatternsStale.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Updates the Patterns-tab total: the number of distinct patterns listed and the total entries
        /// they cover (which, with Drain folding, is the sum across each folded pattern's members). Blank
        /// when nothing has been computed yet.
        /// </summary>
        private void updatePatternsTotal()
        {
            if (_lastPatterns == null || _lastPatterns.Count == 0)
            {
                lblPatternsTotal.Text = "";
                return;
            }

            int patternCount = _lastPatterns.Count;
            long entryTotal = 0;
            foreach (var p in _lastPatterns) { entryTotal += p.Count; }

            lblPatternsTotal.Text = string.Format(
                "{0:###,###,##0} pattern{1} · {2:###,###,##0} entries",
                patternCount, patternCount == 1 ? "" : "s", entryTotal);
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
                        // With Drain folding a pattern covers several regex-templates, so it reads as
                        // hidden when ANY of its member templates is in the hidden set.
                        foreach (var p in patterns)
                        {
                            bool anyHidden = false;
                            foreach (var key in PatternHideKeys(p))
                            {
                                if (_hiddenTemplates.Contains(key)) { anyHidden = true; break; }
                            }
                            p.IsShown = !anyHidden;
                        }
                        _lastPatterns = patterns;
                        setPatternsStale(false);
                        updatePatternsTotal();
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
        /// direction. Defaults to Count ascending, which surfaces the rare one-off patterns first;
        /// clicking a column header changes/flips the sort.
        /// </summary>
        private string _patternSortHeader = "Count";
        private bool _patternSortAscending = true; // Count ascending => rare patterns first

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
        /// no matter what triggered the sort (a header click or the initial Compute Patterns). Each
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
        /// Sorts the patterns table when a column header is clicked. Clicking the current sort column
        /// flips the direction; clicking a different column sorts it (Count/First/Last default to
        /// descending as the most useful first look; Pattern defaults to ascending A-Z). applyPatternSort
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

        // ===================== Pattern summary (with real example) =====================================
        // Builds a shareable per-pattern summary: the matched pattern, its counts, and ONE full real
        // example line from the log (raw content, incl. stack trace). Used to assemble findings.

        /// <summary>One pattern's summary data: metadata + a representative real example.</summary>
        private sealed class PatternSummaryItem
        {
            public LogSearchIndex.LogPattern Pattern;
            public string Example; // the full raw Content of a representative entry (may be multi-line)
            public List<string> Files = new List<string>(); // distinct short file names this pattern appears in
        }

        /// <summary>
        /// Gathers the selected patterns (in display order) with one representative real example each.
        /// The example is the entry with the earliest timestamp in the bucket (stable, "first occurrence").
        /// Also collects the distinct short file names the pattern's entries came from.
        /// </summary>
        private List<PatternSummaryItem> BuildPatternSummaries()
        {
            var selected = new HashSet<LogSearchIndex.LogPattern>(
                patternList.SelectedItems.OfType<LogSearchIndex.LogPattern>());
            var rows = patternList.Items.OfType<LogSearchIndex.LogPattern>().Where(selected.Contains).ToList();

            var items = new List<PatternSummaryItem>(rows.Count);
            foreach (var p in rows)
            {
                var item = new PatternSummaryItem { Pattern = p };
                if (p.Ids != null && p.Ids.Count > 0)
                {
                    // Read the bucket's entries: pick the earliest by timestamp as the example, and
                    // collect the distinct short file names (the grid's disambiguated tail) it spans.
                    NuixLogEntry rep = null;
                    var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in repo.Database.ReadEntries(p.Ids))
                    {
                        if (entry == null) continue;
                        if (rep == null || entry.TimeStamp < rep.TimeStamp) rep = entry;
                        if (!string.IsNullOrEmpty(entry.FileName)) files.Add(entry.FileName);
                    }
                    item.Example = rep?.Content;
                    item.Files = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                }
                items.Add(item);
            }
            return items;
        }

        private void patternCopySummaryRich_Click(object sender, RoutedEventArgs e)
        {
            var items = BuildPatternSummaries();
            if (items.Count == 0) return;
            string md = BuildSummaryMarkdown(items);
            string html = BuildSummaryHtml(items);
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, md);   // text fallback = Markdown (pastes well in chat/docs)
                data.SetData(DataFormats.Text, md);
                data.SetData(DataFormats.Html, WrapCfHtml(html)); // rich paste (Word/Outlook)
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception) { /* clipboard transiently locked; ignore */ }
        }

        private void patternCopySummaryPlain_Click(object sender, RoutedEventArgs e)
        {
            var items = BuildPatternSummaries();
            if (items.Count == 0) return;
            try { Clipboard.SetText(BuildSummaryPlain(items)); }
            catch (Exception) { /* ignore */ }
        }

        private void patternCopySummarySlack_Click(object sender, RoutedEventArgs e)
        {
            var items = BuildPatternSummaries();
            if (items.Count == 0) return;
            try { Clipboard.SetText(BuildSummarySlack(items)); }
            catch (Exception) { /* ignore */ }
        }

        /// <summary>Saves the selected patterns' summary as a standalone HTML document.</summary>
        private void patternSaveSummaryHtml_Click(object sender, RoutedEventArgs e)
        {
            var items = BuildPatternSummaries();
            if (items.Count == 0)
            {
                MessageBox.Show("Select one or more patterns first.");
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Pattern Summary",
                FileName = "log-findings.html",
                Filter = "HTML File (*.html)|*.html",
            };
            if (sfd.ShowDialog() != true) return;

            try
            {
                System.IO.File.WriteAllText(sfd.FileName, BuildSummaryHtmlDocument(items), System.Text.Encoding.UTF8);
                lblStatus.Text = "Summary saved: " + sfd.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the summary:\n" + ex.Message);
            }
        }

        /// <summary>
        /// Wraps the HTML summary fragment in a complete, standalone document (doctype, head, styles)
        /// suitable for saving to a .html file and opening in a browser.
        /// </summary>
        private static string BuildSummaryHtmlDocument(List<PatternSummaryItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"/>");
            sb.AppendLine("<title>Log findings</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b1d21;line-height:1.4}");
            sb.AppendLine("h1{font-size:20px;border-bottom:2px solid #0056E3;padding-bottom:6px}");
            sb.AppendLine("h2{font-size:15px;margin-top:24px;color:#00379B;word-break:break-word}");
            sb.AppendLine("p{margin:4px 0}");
            sb.AppendLine("pre{background:#f4f4f4;border:1px solid #ddd;border-radius:4px;padding:10px;");
            sb.AppendLine("white-space:pre-wrap;word-break:break-word;font-family:Consolas,monospace;font-size:12px;overflow-x:auto}");
            sb.AppendLine("</style></head><body>");
            sb.Append(BuildSummaryHtml(items));   // reuse the same fragment used for rich clipboard
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Slack "mrkdwn" summary. Slack ignores pasted HTML/standard-Markdown, so this emits Slack's
        /// own flavor so it renders when pasted into a message and sent: *bold* (single asterisks; used
        /// as pseudo-headers since Slack has no # headers), `inline code`, and ``` fenced blocks ``` for
        /// the examples. Note Slack only renders a code block from a fence that is on its OWN lines.
        /// </summary>
        private static string BuildSummarySlack(List<PatternSummaryItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("*Log findings*").AppendLine();
            foreach (var it in items)
            {
                var p = it.Pattern;
                // Pseudo-header: bold level + template (inline-code the template so its <PATH>/<Q>
                // tokens and punctuation don't trip Slack's auto-formatting).
                sb.AppendLine($"*{p.DominantLevel}:* `{SlackInline(p.Template)}`");
                sb.AppendLine($"*Count:* {p.Count:N0} ({LevelBreakdown(p)})   |   *First:* {p.FirstSeen:yyyy-MM-dd HH:mm:ss}  *Last:* {p.LastSeen:yyyy-MM-dd HH:mm:ss}");
                if (it.Files != null && it.Files.Count > 0)
                {
                    sb.AppendLine($"*Files ({it.Files.Count}):* {SlackInline(string.Join(", ", it.Files))}");
                }
                if (!string.IsNullOrEmpty(it.Example))
                {
                    // Fenced code block on its own lines so Slack renders monospace on send.
                    sb.AppendLine("```");
                    sb.AppendLine(it.Example.Replace("\r\n", "\n").TrimEnd());
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>Neutralizes backticks in text placed inside a Slack inline-code span.</summary>
        private static string SlackInline(string s) => (s ?? "").Replace("`", "'");

        private static string LevelBreakdown(LogSearchIndex.LogPattern p)
        {
            var parts = new List<string>();
            if (p.Error > 0) parts.Add($"{p.Error:N0} ERROR");
            if (p.Warn > 0) parts.Add($"{p.Warn:N0} WARN");
            if (p.Info > 0) parts.Add($"{p.Info:N0} INFO");
            if (p.Debug > 0) parts.Add($"{p.Debug:N0} DEBUG");
            return parts.Count > 0 ? string.Join(", ", parts) : "";
        }

        /// <summary>Markdown summary: a section per pattern with a fenced code block for the example.</summary>
        private static string BuildSummaryMarkdown(List<PatternSummaryItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Log findings").AppendLine();
            foreach (var it in items)
            {
                var p = it.Pattern;
                sb.AppendLine($"## {p.DominantLevel}: {p.Template}").AppendLine();
                sb.AppendLine($"- **Count:** {p.Count:N0} ({LevelBreakdown(p)})");
                sb.AppendLine($"- **First seen:** {p.FirstSeen:yyyy-MM-dd HH:mm:ss}  **Last seen:** {p.LastSeen:yyyy-MM-dd HH:mm:ss}");
                if (it.Files != null && it.Files.Count > 0)
                {
                    sb.AppendLine($"- **Files ({it.Files.Count}):** {string.Join(", ", it.Files)}");
                }
                sb.AppendLine();
                if (!string.IsNullOrEmpty(it.Example))
                {
                    sb.AppendLine("Example:").AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(it.Example.Replace("\r\n", "\n").TrimEnd());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary>Plain-text summary: same content, no Markdown syntax; example indented.</summary>
        private static string BuildSummaryPlain(List<PatternSummaryItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("LOG FINDINGS").AppendLine();
            foreach (var it in items)
            {
                var p = it.Pattern;
                sb.AppendLine(new string('=', 70));
                sb.AppendLine($"[{p.DominantLevel}] {p.Template}");
                sb.AppendLine($"Count: {p.Count:N0} ({LevelBreakdown(p)})");
                sb.AppendLine($"First seen: {p.FirstSeen:yyyy-MM-dd HH:mm:ss}   Last seen: {p.LastSeen:yyyy-MM-dd HH:mm:ss}");
                if (it.Files != null && it.Files.Count > 0)
                {
                    sb.AppendLine($"Files ({it.Files.Count}): {string.Join(", ", it.Files)}");
                }
                if (!string.IsNullOrEmpty(it.Example))
                {
                    sb.AppendLine("Example:");
                    foreach (var line in it.Example.Replace("\r\n", "\n").TrimEnd().Split('\n'))
                    {
                        sb.AppendLine("    " + line);
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>HTML summary: a section per pattern with the example in a &lt;pre&gt;&lt;code&gt; block.</summary>
        private static string BuildSummaryHtml(List<PatternSummaryItem> items)
        {
            var sb = new StringBuilder();
            sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif\">");
            sb.Append("<h1>Log findings</h1>");
            foreach (var it in items)
            {
                var p = it.Pattern;
                sb.Append($"<h2>{HtmlEscape(p.DominantLevel)}: {HtmlEscape(p.Template)}</h2>");
                sb.Append($"<p><b>Count:</b> {p.Count:N0} ({HtmlEscape(LevelBreakdown(p))})<br/>");
                sb.Append($"<b>First seen:</b> {p.FirstSeen:yyyy-MM-dd HH:mm:ss} &nbsp; <b>Last seen:</b> {p.LastSeen:yyyy-MM-dd HH:mm:ss}</p>");
                if (it.Files != null && it.Files.Count > 0)
                {
                    sb.Append($"<p><b>Files ({it.Files.Count}):</b> {HtmlEscape(string.Join(", ", it.Files))}</p>");
                }
                if (!string.IsNullOrEmpty(it.Example))
                {
                    sb.Append("<pre style=\"background:#f4f4f4;border:1px solid #ddd;padding:8px;");
                    sb.Append("white-space:pre-wrap;font-family:Consolas,monospace;font-size:12px\"><code>");
                    sb.Append(HtmlEscape(it.Example.Replace("\r\n", "\n").TrimEnd()));
                    sb.Append("</code></pre>");
                }
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>
        /// Wraps an HTML fragment in the CF_HTML clipboard format (the byte-offset header Windows apps
        /// like Word/Outlook require for rich paste). Offsets are computed over the UTF-8 byte length.
        /// </summary>
        private static string WrapCfHtml(string htmlFragment)
        {
            const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string preFragment = "<html><body><!--StartFragment-->";
            const string postFragment = "<!--EndFragment--></body></html>";

            // Compute byte offsets. The header itself has a fixed length once formatted (fixed-width fields).
            int headerLen = System.Text.Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
            int startHtml = headerLen;
            int startFragment = startHtml + System.Text.Encoding.UTF8.GetByteCount(preFragment);
            int endFragment = startFragment + System.Text.Encoding.UTF8.GetByteCount(htmlFragment);
            int endHtml = endFragment + System.Text.Encoding.UTF8.GetByteCount(postFragment);

            return string.Format(header, startHtml, endHtml, startFragment, endFragment)
                 + preFragment + htmlFragment + postFragment;
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
        /// Expander toggle on a pattern row: computes (on first expand) and shows that row's
        /// sub-patterns grouped by the current "Expand by" dimension, then toggles the details.
        /// </summary>
        private void patternExpand_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is DependencyObject d)) return;
            var row = FindAncestor<DataGridRow>(d);
            if (row == null || !(row.Item is LogSearchIndex.LogPattern pattern)) return;

            // Toggle: if already open, just collapse.
            if (row.DetailsVisibility == Visibility.Visible)
            {
                row.DetailsVisibility = Visibility.Collapsed;
                return;
            }

            // Compute sub-patterns for the current grouping (recompute if the grouping changed since the
            // last expand for this row - tracked by comparing a stamped dimension).
            string dim = SelectedPatternGroupBy();
            if (pattern.MemberPatterns == null || _memberGroupDimStamp.TryGetValue(pattern, out var stamped) == false || stamped != dim)
            {
                pattern.MemberPatterns = ComputeSubPatterns(pattern, dim);
                _memberGroupDimStamp[pattern] = dim;
            }
            row.DetailsVisibility = Visibility.Visible;
        }

        // Remembers which grouping dimension each pattern's cached MemberPatterns were computed for, so
        // changing the "Expand by" choice recomputes on the next expand.
        private readonly Dictionary<LogSearchIndex.LogPattern, string> _memberGroupDimStamp =
            new Dictionary<LogSearchIndex.LogPattern, string>();

        private string SelectedPatternGroupBy()
        {
            return (cmbPatternGroupBy?.SelectedItem as ComboBoxItem)?.Content as string ?? "Worker";
        }

        /// <summary>
        /// When the "Expand by" choice changes, collapse all open rows and clear cached sub-patterns so
        /// the next expand recomputes with the new dimension.
        /// </summary>
        private void cmbPatternGroupBy_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (patternList == null) return;
            _memberGroupDimStamp.Clear();
            if (_lastPatterns != null)
            {
                foreach (var p in _lastPatterns) p.MemberPatterns = null;
            }
            // Collapse any open details.
            foreach (var item in patternList.Items)
            {
                if (patternList.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow r)
                {
                    r.DetailsVisibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Groups a pattern's entries into sub-patterns by the chosen dimension, on demand. Reads the
        /// bucket's entries once from the DB, then buckets by:
        ///   Worker      -> the worker/restart folder(s) under the job folder (JobIdExtractor.WorkerLabel),
        ///   Exception   -> the first Java throwable class (LogPatternMasker.FirstExceptionClass),
        ///   Sub-template-> the first line with volatile tokens masked (a lighter normalization).
        /// Returns rows ordered by count descending. Bounded to this one pattern's entries.
        /// </summary>
        private IReadOnlyList<LogSearchIndex.MemberPattern> ComputeSubPatterns(LogSearchIndex.LogPattern pattern, string dimension)
        {
            var empty = new List<LogSearchIndex.MemberPattern>();
            if (pattern?.Ids == null || pattern.Ids.Count == 0) return empty;

            var groups = new Dictionary<string, (List<long> Ids, int Info, int Warn, int Error, int Debug)>(StringComparer.Ordinal);
            foreach (var entry in repo.Database.ReadEntries(pattern.Ids))
            {
                if (entry == null) continue;
                string key;
                switch (dimension)
                {
                    case "Exception":
                        key = LogPatternMasker.FirstExceptionClass(entry.Content) ?? "(no exception)";
                        break;
                    case "Sub-template":
                        key = LightSubTemplate(entry.Content);
                        break;
                    case "Worker":
                    default:
                        key = JobIdExtractor.WorkerLabel(entry.FilePath);
                        if (string.IsNullOrEmpty(key)) key = "(unknown)";
                        break;
                }

                if (!groups.TryGetValue(key, out var g)) { g = (new List<long>(), 0, 0, 0, 0); }
                g.Ids.Add(entry.ID);
                switch ((entry.Level ?? "").ToUpperInvariant())
                {
                    case "INFO": g.Info++; break;
                    case "WARN": g.Warn++; break;
                    case "ERROR": g.Error++; break;
                    case "DEBUG": g.Debug++; break;
                }
                groups[key] = g;
            }

            var result = new List<LogSearchIndex.MemberPattern>(groups.Count);
            foreach (var kv in groups)
            {
                result.Add(new LogSearchIndex.MemberPattern
                {
                    Label = kv.Key,
                    Template = "",
                    Count = kv.Value.Ids.Count,
                    Info = kv.Value.Info,
                    Warn = kv.Value.Warn,
                    Error = kv.Value.Error,
                    Debug = kv.Value.Debug,
                    Ids = kv.Value.Ids,
                });
            }
            result.Sort((a, b) => b.Count.CompareTo(a.Count));
            return result;
        }

        // Volatile-token regexes for the "Sub-template" grouping: a lighter normalization than the full
        // pattern masker, so entries that collapsed to one pattern can still split by their finer shape.
        private static readonly System.Text.RegularExpressions.Regex _subHex =
            new System.Text.RegularExpressions.Regex(@"[0-9a-fA-F]{6,}|\b\d+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>First line of the content with hex/numeric tokens blanked, as a coarse sub-template key.</summary>
        private static string LightSubTemplate(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            int nl = content.IndexOfAny(new[] { '\r', '\n' });
            string first = nl >= 0 ? content.Substring(0, nl) : content;
            return _subHex.Replace(first, "#");
        }

        /// <summary>
        /// Double-clicking a sub-pattern (member) row drills the main grid down to exactly that
        /// member's entries - the finer-grained analog of the top-level pattern drill-down. The row is
        /// a Border in the sub-pattern ItemsControl whose DataContext is the MemberPattern.
        /// </summary>
        private void patternMemberRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return; // drill only on double-click
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is LogSearchIndex.MemberPattern member) || member.Ids == null)
            {
                return;
            }
            var ids = member.Ids as IList<long> ?? member.Ids.ToList();
            showIdsInGrid(ids);
            e.Handled = true; // don't bubble to the outer patternList double-click (would re-drill the parent)
        }

        /// <summary>Walks up the visual tree to the nearest ancestor of type T (or null).</summary>
        private static T FindAncestor<T>(DependencyObject from) where T : DependencyObject
        {
            while (from != null && !(from is T))
            {
                from = System.Windows.Media.VisualTreeHelper.GetParent(from);
            }
            return from as T;
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
            refreshFindForNewResults();
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

        // ===================== Find within results =====================================================
        // Marks (does NOT filter) rows in the current result set whose FULL Content matches the Find
        // term. Because the grid is data-virtualized, matching runs as a cancellable background pass
        // that streams the current set's ids -> rows via the DB, collecting the matching ids (for the
        // indicator dots) and the matching ordinals (for next/prev navigation). Row dots are applied by
        // LogEntryGrid, which also marks rows realized later on scroll.

        private System.Threading.CancellationTokenSource _findCts;
        private int _findGeneration;
        // Ordinals (indices into the current ordered-id list) of matching rows, ascending. Drives next/prev.
        private List<int> _findMatchOrdinals = new List<int>();
        private int _findCurrentPos = -1; // index into _findMatchOrdinals, or -1 when none/unset

        private void txtFind_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { clearFind(); return; }
            if (e.Key == Key.Enter)
            {
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                // If the term changed since the last run, (re)run; otherwise just step.
                runFind(thenNavigate: shift ? FindStep.Prev : FindStep.Next);
                return;
            }
        }

        private void findOptions_Changed(object sender, RoutedEventArgs e) => runFind(FindStep.None);
        private void btnFindNext_Click(object sender, RoutedEventArgs e) => stepFind(FindStep.Next);
        private void btnFindPrev_Click(object sender, RoutedEventArgs e) => stepFind(FindStep.Prev);
        private void btnFindClear_Click(object sender, RoutedEventArgs e) => clearFind();

        /// <summary>
        /// Called after a new result set loads. The grid already dropped its old match state; if a Find
        /// term is present, re-run it (mark-only, no navigation) so the indicator reflects the new set;
        /// otherwise reset the Find status/nav to idle.
        /// </summary>
        private void refreshFindForNewResults()
        {
            if (!string.IsNullOrEmpty(txtFind.Text))
            {
                runFind(FindStep.None);
            }
            else
            {
                _findMatchOrdinals = new List<int>();
                _findCurrentPos = -1;
                lblFindStatus.Text = "";
                btnFindNext.IsEnabled = false;
                btnFindPrev.IsEnabled = false;
            }
        }

        private enum FindStep { None, Next, Prev }

        private void clearFind()
        {
            try { _findCts?.Cancel(); } catch { }
            _findGeneration++;
            txtFind.Text = "";
            _findMatchOrdinals = new List<int>();
            _findCurrentPos = -1;
            resultsGrid.ClearFindMatches();
            lblFindStatus.Text = "";
            btnFindNext.IsEnabled = false;
            btnFindPrev.IsEnabled = false;
        }

        /// <summary>
        /// Builds the matcher predicate from the term + option toggles, or null if the term is empty or
        /// (for regex mode) fails to compile. Sets a status message on regex-compile failure.
        /// </summary>
        private Func<string, bool> BuildFindPredicate(string term)
        {
            if (string.IsNullOrEmpty(term)) return null;

            bool caseSensitive = chkFindCase.IsChecked == true;
            if (chkFindRegex.IsChecked == true)
            {
                try
                {
                    var opts = System.Text.RegularExpressions.RegexOptions.Compiled;
                    if (!caseSensitive) opts |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                    var rx = new System.Text.RegularExpressions.Regex(term, opts);
                    return s => s != null && rx.IsMatch(s);
                }
                catch (ArgumentException ex)
                {
                    lblFindStatus.Text = "bad regex";
                    lblFindStatus.ToolTip = ex.Message;
                    return null;
                }
            }
            // Literal substring match.
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return s => s != null && s.IndexOf(term, cmp) >= 0;
        }

        /// <summary>
        /// Runs the find over the current result set on a background task, marks matching rows, updates
        /// the count, and optionally navigates to the first/next/prev match when done.
        /// </summary>
        private void runFind(FindStep thenNavigate)
        {
            lblFindStatus.ToolTip = null;
            string term = txtFind.Text;
            var predicate = BuildFindPredicate(term);
            if (predicate == null)
            {
                // Empty term clears; bad regex leaves the "bad regex" status set by BuildFindPredicate.
                if (string.IsNullOrEmpty(term)) { clearFind(); }
                else { resultsGrid.ClearFindMatches(); _findMatchOrdinals = new List<int>(); _findCurrentPos = -1; btnFindNext.IsEnabled = btnFindPrev.IsEnabled = false; }
                return;
            }

            var ids = resultsGrid.CurrentOrderedIds;
            if (ids == null || ids.Count == 0)
            {
                lblFindStatus.Text = "0 matches";
                resultsGrid.ClearFindMatches();
                _findMatchOrdinals = new List<int>(); _findCurrentPos = -1;
                btnFindNext.IsEnabled = btnFindPrev.IsEnabled = false;
                return;
            }

            // Cancel any in-flight find and start a fresh generation.
            try { _findCts?.Cancel(); } catch { }
            var cts = new System.Threading.CancellationTokenSource();
            _findCts = cts;
            int gen = ++_findGeneration;
            var token = cts.Token;

            // Snapshot the id list (ordinal order) so the background pass has a stable view.
            var idList = ids.ToList();
            lblFindStatus.Text = "finding...";

            Task.Run(() =>
            {
                var matchIds = new HashSet<long>();
                // Map id -> ordinal for this run (ReadEntries doesn't preserve input order).
                var idToOrdinal = new Dictionary<long, int>(idList.Count);
                for (int i = 0; i < idList.Count; i++) idToOrdinal[idList[i]] = i;

                // Stream in chunks so a huge set doesn't build one giant IN(...) and so we can cancel.
                const int chunk = 2000;
                for (int start = 0; start < idList.Count; start += chunk)
                {
                    token.ThrowIfCancellationRequested();
                    var slice = idList.Skip(start).Take(chunk);
                    foreach (var entry in repo.Database.ReadEntries(slice))
                    {
                        if (entry?.Content != null && predicate(entry.Content))
                        {
                            matchIds.Add(entry.ID);
                        }
                    }
                }

                // Derive ordinals of matches (ascending) from the id->ordinal map.
                var ordinals = new List<int>(matchIds.Count);
                foreach (var id in matchIds)
                {
                    if (idToOrdinal.TryGetValue(id, out int ord)) ordinals.Add(ord);
                }
                ordinals.Sort();
                return (matchIds, ordinals);
            }, token).ContinueWith(t =>
            {
                if (gen != _findGeneration) return; // superseded by a newer find
                if (t.IsCanceled) return;
                if (t.IsFaulted)
                {
                    lblFindStatus.Text = "find error";
                    lblFindStatus.ToolTip = t.Exception?.GetBaseException().Message;
                    return;
                }

                var (matchIds, ordinals) = t.Result;
                _findMatchOrdinals = ordinals;
                _findCurrentPos = -1;
                resultsGrid.SetFindMatches(matchIds);

                int count = ordinals.Count;
                lblFindStatus.Text = count == 1 ? "1 match" : count + " matches";
                btnFindNext.IsEnabled = count > 0;
                btnFindPrev.IsEnabled = count > 0;

                if (count > 0 && thenNavigate != FindStep.None)
                {
                    stepFind(thenNavigate);
                }
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Moves to the next/previous match (wrapping) and scrolls to it. Re-runs the find first if the
        /// term changed but hasn't been evaluated yet (handled by runFind calling stepFind on completion).
        /// </summary>
        private void stepFind(FindStep dir)
        {
            var ordinals = _findMatchOrdinals;
            if (ordinals == null || ordinals.Count == 0) return;

            if (dir == FindStep.Next)
            {
                _findCurrentPos = (_findCurrentPos + 1) % ordinals.Count;
            }
            else if (dir == FindStep.Prev)
            {
                _findCurrentPos = (_findCurrentPos <= 0) ? ordinals.Count - 1 : _findCurrentPos - 1;
            }
            else return;

            int ordinal = ordinals[_findCurrentPos];
            var ids = resultsGrid.CurrentOrderedIds;
            if (ids == null || ordinal < 0 || ordinal >= ids.Count) return;

            resultsGrid.SelectAndScrollToId(ids[ordinal]);
            lblFindStatus.Text = $"{_findCurrentPos + 1} of {ordinals.Count}";
        }
    }
}
