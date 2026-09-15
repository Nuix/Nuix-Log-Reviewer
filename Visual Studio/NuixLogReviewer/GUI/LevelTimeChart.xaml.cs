using System;
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

        private bool _dragging;
        private double _dragStartX;

        // Remembered so they can be re-applied on resize / redraw.
        private long? _markerTicks;
        private long? _visibleStartTicks;
        private long? _visibleEndTicks;

        /// <summary>Raised when the user finishes dragging out a time region on the chart.</summary>
        public event Action<long, long> TimeRangeSelected;

        static LevelTimeChart()
        {
            InfoBrush.Freeze();
            WarnBrush.Freeze();
            ErrorBrush.Freeze();
        }

        public LevelTimeChart()
        {
            InitializeComponent();
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

        private void chartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            redraw();
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

            for (int b = 0; b < buckets; b++)
            {
                int info = counts[b, 0];
                int warn = counts[b, 1];
                int error = counts[b, 2];
                int total = info + warn + error;
                if (total == 0) continue;

                double x = b * bucketW;
                double colW = Math.Max(1.0, bucketW); // avoid sub-pixel gaps
                double scale = h / maxTotal;

                // Stack from the bottom up: ERROR, then WARN, then INFO on top.
                double y = h;
                y = addSegment(x, y, colW, error * scale, ErrorBrush);
                y = addSegment(x, y, colW, warn * scale, WarnBrush);
                y = addSegment(x, y, colW, info * scale, InfoBrush);
            }

            applyOverlays();
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

            // Ignore tiny drags (treat as a click, not a selection).
            if (right - left < 3)
            {
                selectionRect.Visibility = Visibility.Collapsed;
                readout.Visibility = Visibility.Collapsed;
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
    }
}
