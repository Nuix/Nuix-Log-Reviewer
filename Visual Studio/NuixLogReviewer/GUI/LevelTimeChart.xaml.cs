using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NuixLogReviewer.LogRepository;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// A short, full-width stacked bar strip charting INFO/WARN/ERROR counts over the time span of
    /// the current filtered set. Supports dragging horizontally to select a time region, which is
    /// reported via <see cref="TimeRangeSelected"/> as a (minTicks, maxTicks) pair.
    /// </summary>
    public partial class LevelTimeChart : UserControl
    {
        // Colors chosen to match the grid row highlights (INFO green, WARN yellow, ERROR red).
        private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x71));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xC2, 0x29));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x3A, 0x3A));

        private LogSearchIndex.TimeSeries _series;

        // Job lifespan overlay (brackets above the bars). Kept separate from the bars so they can be
        // toggled/updated independently, and re-laid-out on resize.
        private System.Collections.Generic.IList<LogSearchIndex.JobInfo> _jobSpans;
        private readonly System.Collections.Generic.List<UIElement> _jobElements = new System.Collections.Generic.List<UIElement>();
        // Job id currently selected in the Jobs grid, emphasized on the chart. Null = none.
        private string _selectedJobId;

        // Overlay geometry: a small lane band pinned to the TOP of the strip so the stacked bars below
        // stay readable. Each concurrent job gets its own lane; lanes are capped and overflow is noted.
        // Concurrency is typically low, so lanes are drawn fairly tall for easy hovering/reading.
        private const double JobLaneHeight = 7.0;   // thickness of each job bar
        private const double JobLaneGap = 2.0;      // vertical gap between lanes
        private const int JobMaxLanes = 3;          // cap so a big worker fan-out doesn't eat the strip
        private static readonly Brush JobBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x3D, 0x7C, 0xFF));       // True Blue tint (unselected)
        private static readonly Brush JobSelectedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x56, 0xE3)); // full True Blue (selected)
        private static readonly Brush JobSelectedStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x37, 0x9B));
        private static readonly Brush JobOverflowBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x37, 0x9B));

        private bool _dragging;
        private double _dragStartX;
        // When true, bar heights are log-compressed (see redraw): height ~ log(1+total), split across
        // levels by proportion. Preserves the stacked INFO/WARN/ERROR look while keeping small buckets
        // visible next to very tall ones. Toggled by the corner "Log" checkbox (default on; see XAML).
        private bool _logScale = true;

        // Remembered so they can be re-applied on resize / redraw.
        private long? _markerTicks;
        private long? _visibleStartTicks;
        private long? _visibleEndTicks;

        /// <summary>Raised when the user finishes dragging out a time region on the chart.</summary>
        public event Action<long, long> TimeRangeSelected;

        /// <summary>
        /// Raised on a single click (as opposed to a drag) with the clicked event-time (ticks). The
        /// host uses it to scroll the grid to the nearest log event at or before that time.
        /// </summary>
        public event Action<long> TimeClicked;

        /// <summary>
        /// Raised when the chart's desired time-slice (bucket) count changes - either when it first
        /// gets a width or when a resize crosses into a different count. The host re-fetches the time
        /// series at this resolution and calls <see cref="SetData"/>. Debounced so a resize drag doesn't
        /// spam re-queries.
        /// </summary>
        public event Action<int> ResolutionChanged;

        // Dynamic time-slice resolution: aim for ~4px per bucket (bars are gapless, so this reads as a
        // density strip and stays legible), clamped to a sensible range so a tiny window still shows a
        // useful shape and a huge one doesn't create excessive work/elements.
        private const double TargetBucketPx = 4.0;
        private const int MinBuckets = 60;
        private const int MaxBuckets = 1000;
        /// <summary>Fallback bucket count used before the control has a real (laid-out) width.</summary>
        public const int DefaultBuckets = 120;

        // Debounce for resize-driven resolution changes.
        private readonly System.Windows.Threading.DispatcherTimer _resizeDebounce;
        // The bucket count we last asked the host to fetch, so we only re-query when it actually changes.
        private int _lastRequestedBuckets = 0;

        static LevelTimeChart()
        {
            InfoBrush.Freeze();
            WarnBrush.Freeze();
            ErrorBrush.Freeze();
            JobBrush.Freeze();
            JobSelectedBrush.Freeze();
            JobSelectedStroke.Freeze();
            JobOverflowBrush.Freeze();
        }

        public LevelTimeChart()
        {
            InitializeComponent();

            // Coalesce rapid SizeChanged events during a resize/splitter drag into a single resolution
            // check once the size settles.
            _resizeDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _resizeDebounce.Tick += (s, e) =>
            {
                _resizeDebounce.Stop();
                RaiseResolutionIfChanged();
            };
        }

        /// <summary>
        /// The desired number of time-slices (buckets) for a given rendered width: ~1 per
        /// <see cref="TargetBucketPx"/> pixels, clamped to [<see cref="MinBuckets"/>,
        /// <see cref="MaxBuckets"/>]. Returns <see cref="DefaultBuckets"/> when width is unknown (0),
        /// i.e. before layout.
        /// </summary>
        public static int DesiredBucketCount(double width)
        {
            if (double.IsNaN(width) || width <= 0) { return DefaultBuckets; }
            int raw = (int)Math.Round(width / TargetBucketPx);
            if (raw < MinBuckets) { return MinBuckets; }
            if (raw > MaxBuckets) { return MaxBuckets; }
            return raw;
        }

        /// <summary>
        /// The bucket count the chart currently wants, based on its laid-out width. The host uses this
        /// for the initial fetch; subsequent changes come via <see cref="ResolutionChanged"/>.
        /// </summary>
        public int CurrentDesiredBucketCount => DesiredBucketCount(chartCanvas.ActualWidth);

        /// <summary>
        /// Records the bucket count the host most recently fetched at, so a subsequent resize only
        /// raises <see cref="ResolutionChanged"/> when it lands on a genuinely different count. The host
        /// calls this whenever it fetches a series (initial load and resolution-change re-fetches).
        /// </summary>
        public void MarkRequested(int buckets)
        {
            _lastRequestedBuckets = buckets;
        }

        /// <summary>
        /// Raises <see cref="ResolutionChanged"/> if the width-derived bucket count differs from the
        /// last one we requested. Records the new value so we don't re-raise for the same count.
        /// </summary>
        private void RaiseResolutionIfChanged()
        {
            int desired = CurrentDesiredBucketCount;
            if (desired != _lastRequestedBuckets)
            {
                _lastRequestedBuckets = desired;
                ResolutionChanged?.Invoke(desired);
            }
        }

        /// <summary>Shows a placeholder / loading message and clears any drawn bars.</summary>
        public void ShowPlaceholder(string text)
        {
            _series = null;
            lblPlaceholder.Text = text ?? "";
            lblPlaceholder.Visibility = Visibility.Visible;
            clearBars();
            selectionRect.Visibility = Visibility.Collapsed;
            hoverLine.Visibility = Visibility.Collapsed;
            readout.Visibility = Visibility.Collapsed;
        }

        /// <summary>Sets the data to chart and redraws. Pass null/empty to show "no data".</summary>
        public void SetData(LogSearchIndex.TimeSeries series)
        {
            _series = series;
            _markerTicks = null;
            _visibleStartTicks = null;
            _visibleEndTicks = null;
            // A new result set invalidates any previously-overlaid job spans (they were computed for
            // the old set). The user recomputes Jobs for the new set; keeps the overlay consistent
            // with the filtered set like the rest of the app.
            _jobSpans = null;
            _selectedJobId = null;
            clearJobElements();
            selectionRect.Visibility = Visibility.Collapsed;
            hoverLine.Visibility = Visibility.Collapsed;
            readout.Visibility = Visibility.Collapsed;
            selectionMarker.Visibility = Visibility.Collapsed;
            visibleBand.Visibility = Visibility.Collapsed;

            if (_series == null || _series.IsEmpty)
            {
                lblPlaceholder.Text = "No data in range";
                lblPlaceholder.Visibility = Visibility.Visible;
                clearBars();
                return;
            }

            lblPlaceholder.Visibility = Visibility.Collapsed;
            redraw();
        }

        /// <summary>
        /// Replaces just the charted series with a re-bucketed version of the SAME data (used when a
        /// resize changes the desired time-slice count), preserving overlays - job spans, the selected
        /// job, the selection marker and the visible-range band - which <see cref="SetData"/> would
        /// otherwise reset. Redraw re-applies those overlays from their retained state.
        /// </summary>
        public void UpdateResolution(LogSearchIndex.TimeSeries series)
        {
            if (series == null || series.IsEmpty)
            {
                // Nothing to show at this resolution; leave the existing chart as-is rather than blanking.
                return;
            }
            _series = series;
            lblPlaceholder.Visibility = Visibility.Collapsed;
            redraw();
        }

        private void chartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Immediate relayout of the existing series to the new width keeps the strip responsive
            // during a drag; the debounced timer then re-fetches at a finer/coarser resolution once
            // the size settles (only if the desired bucket count actually changed).
            redraw();
            if (e.WidthChanged)
            {
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
            }
        }

        private void clearBars()
        {
            // Remove drawn bars but keep the persistent overlay elements (selection, hover, readout).
            for (int i = chartCanvas.Children.Count - 1; i >= 0; i--)
            {
                var child = chartCanvas.Children[i];
                if (ReferenceEquals(child, selectionRect) ||
                    ReferenceEquals(child, hoverLine) ||
                    ReferenceEquals(child, readout) ||
                    ReferenceEquals(child, selectionMarker) ||
                    ReferenceEquals(child, visibleBand))
                {
                    continue;
                }
                chartCanvas.Children.RemoveAt(i);
            }
        }

        private void redraw()
        {
            clearBars();
            if (_series == null || _series.IsEmpty) return;

            double w = chartCanvas.ActualWidth;
            double h = chartCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            int buckets = _series.Buckets;
            var counts = _series.Counts;

            // Guard against a malformed series (bucket count not matching the counts array). Without
            // real per-bucket data there are no bars to draw; overlays (marker/band) still apply.
            if (counts == null || counts.GetLength(0) < buckets || counts.GetLength(1) < 3)
            {
                applyOverlays();
                return;
            }

            // Find the tallest bucket (total across levels) to scale bar heights.
            int maxTotal = 1;
            for (int b = 0; b < buckets; b++)
            {
                int total = counts[b, 0] + counts[b, 1] + counts[b, 2];
                if (total > maxTotal) maxTotal = total;
            }

            double bucketW = w / buckets;

            // Height scale. Linear: segment height is proportional to its count. Log: the whole bar's
            // height is proportional to log(1+total) (so a huge bucket doesn't flatten the small ones),
            // and that bar height is split across levels by each level's PROPORTION of the total - you
            // can't log-scale each stacked segment independently (log(a+b) != log a + log b), so we
            // log-scale the total and keep the segments proportional.
            double logMax = Math.Log(1 + maxTotal);

            for (int b = 0; b < buckets; b++)
            {
                int info = counts[b, 0];
                int warn = counts[b, 1];
                int error = counts[b, 2];
                int total = info + warn + error;
                if (total == 0) continue;

                double x = b * bucketW;
                double colW = Math.Max(1.0, bucketW); // avoid sub-pixel gaps

                double errH, warnH, infoH;
                if (_logScale)
                {
                    // Bar height from the log of the total, then divide by level proportion.
                    double barH = logMax > 0 ? Math.Log(1 + total) / logMax * h : 0;
                    errH = barH * error / total;
                    warnH = barH * warn / total;
                    infoH = barH * info / total;
                }
                else
                {
                    double scale = h / maxTotal;
                    errH = error * scale;
                    warnH = warn * scale;
                    infoH = info * scale;
                }

                // Stack from the bottom up: ERROR, then WARN, then INFO on top.
                double y = h;
                y = addSegment(x, y, colW, errH, ErrorBrush);
                y = addSegment(x, y, colW, warnH, WarnBrush);
                y = addSegment(x, y, colW, infoH, InfoBrush);
            }

            applyOverlays();
            drawJobSpans();
        }

        /// <summary>Toggles log-scaled bar heights and redraws.</summary>
        private void chkLogScale_Click(object sender, RoutedEventArgs e)
        {
            _logScale = chkLogScale.IsChecked == true;
            redraw();
        }

        private double addSegment(double x, double bottomY, double width, double segHeight, Brush fill)
        {
            if (segHeight <= 0) return bottomY;
            double top = bottomY - segHeight;
            var rect = new Rectangle
            {
                Width = width,
                Height = segHeight,
                Fill = fill
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, top);
            // Draw bars beneath all overlay elements (they are added at the bottom of the Z-order).
            chartCanvas.Children.Insert(0, rect);
            return top;
        }

        // ---- Drag-to-select ----

        private void chartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_series == null || _series.IsEmpty) return;
            _dragging = true;
            _dragStartX = clampX(e.GetPosition(chartCanvas).X);
            chartCanvas.CaptureMouse();

            Canvas.SetLeft(selectionRect, _dragStartX);
            selectionRect.Width = 0;
            selectionRect.Height = chartCanvas.ActualHeight;
            selectionRect.Visibility = Visibility.Visible;
        }

        private void chartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_series == null || _series.IsEmpty)
            {
                return;
            }

            double x = clampX(e.GetPosition(chartCanvas).X);

            if (_dragging)
            {
                double left = Math.Min(_dragStartX, x);
                double width = Math.Abs(x - _dragStartX);
                Canvas.SetLeft(selectionRect, left);
                selectionRect.Width = width;
                selectionRect.Height = chartCanvas.ActualHeight;

                // Readout shows the selection start and end.
                long a = xToTicks(left);
                long b = xToTicks(left + width);
                showReadout(x, string.Format("{0:yyyy-MM-dd HH:mm:ss} \u2192 {1:yyyy-MM-dd HH:mm:ss}",
                    new DateTime(a), new DateTime(b)));
                hoverLine.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Hover: vertical guide line + the time under the cursor.
                Canvas.SetLeft(hoverLine, x);
                hoverLine.Height = chartCanvas.ActualHeight;
                hoverLine.Visibility = Visibility.Visible;

                showReadout(x, new DateTime(xToTicks(x)).ToString("yyyy-MM-dd HH:mm:ss"));
            }
        }

        /// <summary>
        /// Positions the floating readout near the given cursor X (kept within the canvas bounds)
        /// and sets its text.
        /// </summary>
        private void showReadout(double cursorX, string text)
        {
            readoutText.Text = text;
            readout.Visibility = Visibility.Visible;

            // Measure so we can keep it on-screen.
            readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double rw = readout.DesiredSize.Width;

            double left = cursorX + 8;
            if (left + rw > chartCanvas.ActualWidth)
            {
                left = cursorX - 8 - rw; // flip to the left of the cursor near the right edge
            }
            if (left < 0) left = 0;

            Canvas.SetLeft(readout, left);
            Canvas.SetTop(readout, 1);
        }

        private void hideHoverVisuals()
        {
            hoverLine.Visibility = Visibility.Collapsed;
            if (!_dragging)
            {
                readout.Visibility = Visibility.Collapsed;
            }
        }

        private void chartCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            chartCanvas.ReleaseMouseCapture();

            double x = clampX(e.GetPosition(chartCanvas).X);
            double left = Math.Min(_dragStartX, x);
            double right = Math.Max(_dragStartX, x);

            // Tiny movement => treat as a click (not a range selection): report the clicked time so
            // the host can scroll the grid to the nearest log event at or before that time.
            if (right - left < 3)
            {
                selectionRect.Visibility = Visibility.Collapsed;
                readout.Visibility = Visibility.Collapsed;
                TimeClicked?.Invoke(xToTicks(x));
                return;
            }

            long minTicks = xToTicks(left);
            long maxTicks = xToTicks(right);
            if (maxTicks < minTicks) { var t = minTicks; minTicks = maxTicks; maxTicks = t; }

            TimeRangeSelected?.Invoke(minTicks, maxTicks);
        }

        private void chartCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            // If the mouse leaves mid-drag without capture, cancel the in-progress selection visuals.
            if (_dragging && !chartCanvas.IsMouseCaptured)
            {
                _dragging = false;
                selectionRect.Visibility = Visibility.Collapsed;
            }
            hideHoverVisuals();
        }

        private double clampX(double x)
        {
            if (x < 0) return 0;
            if (x > chartCanvas.ActualWidth) return chartCanvas.ActualWidth;
            return x;
        }

        /// <summary>Maps a canvas X pixel to an event-time tick within the charted span.</summary>
        private long xToTicks(double x)
        {
            double w = chartCanvas.ActualWidth;
            if (w <= 0 || _series == null || _series.MinTicks == null || _series.MaxTicks == null)
            {
                return _series?.MinTicks ?? 0;
            }

            long min = _series.MinTicks.Value;
            long max = _series.MaxTicks.Value;
            double frac = x / w;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            return min + (long)(frac * (max - min));
        }

        /// <summary>Maps an event-time tick to a canvas X pixel within the charted span (null if out of range/no data).</summary>
        private double? ticksToX(long ticks)
        {
            double w = chartCanvas.ActualWidth;
            if (w <= 0 || _series == null || _series.MinTicks == null || _series.MaxTicks == null)
            {
                return null;
            }
            long min = _series.MinTicks.Value;
            long max = _series.MaxTicks.Value;
            if (max <= min) return 0;
            if (ticks < min || ticks > max) return null;
            return (double)(ticks - min) / (max - min) * w;
        }

        /// <summary>
        /// Marks the event time of the currently selected grid row on the chart (a vertical line).
        /// Pass null to clear.
        /// </summary>
        public void SetSelectionMarker(long? ticks)
        {
            _markerTicks = ticks;
            applyOverlays();
        }

        /// <summary>
        /// Shades the time span of the rows currently visible in the grid. Pass nulls to clear.
        /// </summary>
        public void SetVisibleRange(long? startTicks, long? endTicks)
        {
            _visibleStartTicks = startTicks;
            _visibleEndTicks = endTicks;
            applyOverlays();
        }

        /// <summary>Positions the selection marker and visible-range band based on current data/size.</summary>
        private void applyOverlays()
        {
            double h = chartCanvas.ActualHeight;

            // Selection marker
            if (_markerTicks.HasValue && ticksToX(_markerTicks.Value) is double mx)
            {
                Canvas.SetLeft(selectionMarker, mx);
                selectionMarker.Height = h;
                selectionMarker.Visibility = Visibility.Visible;
            }
            else
            {
                selectionMarker.Visibility = Visibility.Collapsed;
            }

            // Visible-rows band
            if (_visibleStartTicks.HasValue && _visibleEndTicks.HasValue &&
                _series != null && !_series.IsEmpty)
            {
                // Clamp the band to the charted span even if the visible rows extend beyond it.
                long min = _series.MinTicks.Value, max = _series.MaxTicks.Value;
                long s = Math.Max(min, Math.Min(_visibleStartTicks.Value, _visibleEndTicks.Value));
                long en = Math.Min(max, Math.Max(_visibleStartTicks.Value, _visibleEndTicks.Value));
                double? sx = ticksToX(s);
                double? ex = ticksToX(en);
                if (sx.HasValue && ex.HasValue)
                {
                    Canvas.SetLeft(visibleBand, sx.Value);
                    visibleBand.Width = Math.Max(1.0, ex.Value - sx.Value);
                    visibleBand.Height = h;
                    visibleBand.Visibility = Visibility.Visible;
                }
                else
                {
                    visibleBand.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                visibleBand.Visibility = Visibility.Collapsed;
            }
        }

        // ---- Job lifespan overlay ----

        /// <summary>
        /// Overlays the given worker job lifespans as brackets in a small lane band at the top of the
        /// strip. Pass null/empty to clear. Concurrent (time-overlapping) jobs are placed on separate
        /// lanes so parallelism is visible; lanes are capped (JobMaxLanes) and any jobs that don't fit
        /// are drawn merged into a final "overflow" lane so the strip height stays bounded.
        /// </summary>
        public void SetJobSpans(System.Collections.Generic.IList<LogSearchIndex.JobInfo> jobs)
        {
            _jobSpans = jobs;
            drawJobSpans();
        }

        /// <summary>
        /// Emphasizes the bracket for the given job id (full brand color + outline, drawn on top) so it
        /// visually corresponds to the row selected in the Jobs grid. Pass null to clear the emphasis.
        /// </summary>
        public void SetSelectedJob(string jobId)
        {
            if (string.Equals(_selectedJobId, jobId, StringComparison.Ordinal)) return;
            _selectedJobId = jobId;
            drawJobSpans();
        }

        private void clearJobElements()
        {
            foreach (var el in _jobElements)
            {
                chartCanvas.Children.Remove(el);
            }
            _jobElements.Clear();
        }

        /// <summary>
        /// Draws the job-span brackets. Greedy lane packing: jobs are taken in start-time order and
        /// placed on the first lane whose last span ends before this job starts; concurrent jobs thus
        /// stack onto separate lanes. Beyond JobMaxLanes everything collapses to one overflow lane.
        /// Spans are clamped to the charted time window so partial-overlap jobs still show.
        /// </summary>
        private void drawJobSpans()
        {
            clearJobElements();

            if (_jobSpans == null || _jobSpans.Count == 0) return;
            if (_series == null || _series.IsEmpty) return;

            double w = chartCanvas.ActualWidth;
            if (w <= 0) return;

            long min = _series.MinTicks.Value;
            long max = _series.MaxTicks.Value;
            if (max <= min) return;

            // Order by start so greedy lane assignment yields a stable, readable packing.
            var ordered = _jobSpans.OrderBy(j => j.FirstSeen.Ticks).ToList();

            // laneEndX[i] = right pixel edge of the last job placed on lane i.
            var laneEndX = new System.Collections.Generic.List<double>();
            Rectangle selectedBar = null;

            foreach (var job in ordered)
            {
                // Clamp span to the charted window.
                long s = Math.Max(min, Math.Min(job.FirstSeen.Ticks, job.LastSeen.Ticks));
                long e = Math.Min(max, Math.Max(job.FirstSeen.Ticks, job.LastSeen.Ticks));
                if (e < min || s > max) continue; // entirely outside the window

                double x1 = (double)(s - min) / (max - min) * w;
                double x2 = (double)(e - min) / (max - min) * w;
                double barW = Math.Max(2.0, x2 - x1); // keep very short jobs visible

                // Find the first lane free at x1 (a small gap avoids touching bars looking merged).
                int lane = -1;
                for (int i = 0; i < laneEndX.Count; i++)
                {
                    if (x1 >= laneEndX[i] + 2.0) { lane = i; break; }
                }
                if (lane == -1)
                {
                    if (laneEndX.Count < JobMaxLanes) { lane = laneEndX.Count; laneEndX.Add(0); }
                    else { lane = JobMaxLanes - 1; } // overflow: share the last lane
                }
                laneEndX[lane] = x1 + barW;

                bool overflow = (lane == JobMaxLanes - 1) && laneEndX.Count == JobMaxLanes && ordered.Count > JobMaxLanes;
                double top = 1.0 + lane * (JobLaneHeight + JobLaneGap);

                bool selected = _selectedJobId != null && string.Equals(job.JobId, _selectedJobId, StringComparison.Ordinal);

                var bar = new Rectangle
                {
                    Width = barW,
                    Height = JobLaneHeight,
                    RadiusX = 2.0,
                    RadiusY = 2.0,
                    Fill = selected ? JobSelectedBrush : (overflow ? JobOverflowBrush : JobBrush),
                    Stroke = selected ? JobSelectedStroke : null,
                    StrokeThickness = selected ? 1.0 : 0.0,
                    ToolTip = string.Format("{0}\n{1:yyyy-MM-dd HH:mm:ss} \u2192 {2:yyyy-MM-dd HH:mm:ss}  ({3})\n{4:N0} entries, {5:N0} warns, {6:N0} errors",
                        job.JobId, job.FirstSeen, job.LastSeen, job.DurationText,
                        job.EntryCount, job.WarnCount, job.ErrorCount),
                };
                Canvas.SetLeft(bar, x1);
                Canvas.SetTop(bar, top);
                if (selected)
                {
                    // Defer so the selected bar (with its stroke) draws on top of any later neighbors.
                    selectedBar = bar;
                }
                else
                {
                    chartCanvas.Children.Add(bar);
                    _jobElements.Add(bar);
                }
            }

            if (selectedBar != null)
            {
                chartCanvas.Children.Add(selectedBar);
                _jobElements.Add(selectedBar);
            }
        }
    }
}
