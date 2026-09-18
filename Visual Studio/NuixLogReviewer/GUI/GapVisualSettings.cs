using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// Live, in-app settings for the log grid's inter-row time-gap visual cue. A single shared instance
    /// (<see cref="Instance"/>) is bound by the grid (border/column visibility) and by the gap
    /// converters (threshold). Being INotifyPropertyChanged, tweaking the threshold or toggles updates
    /// the grid immediately. Not persisted - it's a view aid tuned per session.
    /// </summary>
    public sealed class GapVisualSettings : INotifyPropertyChanged
    {
        public static GapVisualSettings Instance { get; } = new GapVisualSettings();

        private bool _showGapBorder = false;
        private bool _showGapColumn = true;
        private double _highlightThresholdSeconds = 300.0;
        private double _floorSeconds = 60.0;

        /// <summary>Whether to color the inter-row bottom border by the gap to the previous row.</summary>
        public bool ShowGapBorder
        {
            get => _showGapBorder;
            set { if (_showGapBorder != value) { _showGapBorder = value; Raise(nameof(ShowGapBorder)); RaiseChanged(); } }
        }

        /// <summary>Whether to show the numeric "Δ" (gap-to-previous) column.</summary>
        public bool ShowGapColumn
        {
            get => _showGapColumn;
            set { if (_showGapColumn != value) { _showGapColumn = value; Raise(nameof(ShowGapColumn)); RaiseChanged(); } }
        }

        /// <summary>
        /// Gap size (in seconds) at which the cue reaches full "large gap" emphasis (red). The color
        /// ramp runs logarithmically from a small floor up to this threshold. Clamped to a sane range.
        /// </summary>
        public double HighlightThresholdSeconds
        {
            get => _highlightThresholdSeconds;
            set
            {
                double v = value;
                if (v < 1) v = 1;
                if (v > 86400) v = 86400; // cap at a day
                if (Math.Abs(_highlightThresholdSeconds - v) > double.Epsilon)
                {
                    _highlightThresholdSeconds = v;
                    Raise(nameof(HighlightThresholdSeconds));
                    RaiseChanged();
                }
            }
        }

        /// <summary>
        /// Below this gap (seconds) NO cue is drawn - only gaps of at least this long get a colored
        /// line. Live-configurable. Kept strictly below <see cref="HighlightThresholdSeconds"/> so the
        /// ramp has room; clamped to a sane range.
        /// </summary>
        public double FloorSeconds
        {
            get => _floorSeconds;
            set
            {
                double v = value;
                if (v < 0) v = 0;
                if (v > 86400) v = 86400;
                if (Math.Abs(_floorSeconds - v) > double.Epsilon)
                {
                    _floorSeconds = v;
                    Raise(nameof(FloorSeconds));
                    RaiseChanged();
                }
            }
        }

        /// <summary>Raised when any setting changes, so converters can ask the grid to re-render.</summary>
        public event EventHandler Changed;
        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Maps a row's <c>GapToPrevious</c> (TimeSpan?) to a border brush: transparent below the floor,
    /// then a logarithmic ramp from cool (small gap) to warm/red as the gap approaches the configured
    /// highlight threshold. Null gap (first row) => transparent.
    /// </summary>
    public sealed class GapToBrushConverter : IValueConverter
    {
        // Endpoint colors of the ramp (cool teal -> hot red), matching the app's status palette feel.
        private static readonly Color Cool = Color.FromRgb(0x13, 0x91, 0xA8); // low gap
        private static readonly Color Hot = Color.FromRgb(0xD2, 0x3A, 0x34);  // >= threshold

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is TimeSpan gap)) { return Brushes.Transparent; }

            var s = GapVisualSettings.Instance;
            double secs = gap.TotalSeconds;
            if (secs < s.FloorSeconds) { return Brushes.Transparent; }

            // Logarithmic position from floor..threshold => 0..1.
            double lo = Math.Log10(s.FloorSeconds);
            double hi = Math.Log10(Math.Max(s.HighlightThresholdSeconds, s.FloorSeconds * 10));
            double t = (Math.Log10(secs) - lo) / (hi - lo);
            if (t < 0) t = 0; if (t > 1) t = 1;

            byte Lerp(byte a, byte b) => (byte)(a + (b - a) * t);
            var c = Color.FromRgb(Lerp(Cool.R, Hot.R), Lerp(Cool.G, Hot.G), Lerp(Cool.B, Hot.B));
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// MultiBinding variant of <see cref="GapToBrushConverter"/>: values are [GapToPrevious (TimeSpan?),
    /// HighlightThresholdSeconds (double)]. Binding to the live threshold as a second value makes the
    /// row border re-evaluate (and repaint) the instant the user drags the scale control - a plain
    /// single binding to GapToPrevious wouldn't, since that value doesn't change.
    /// </summary>
    public sealed class GapToBrushMultiConverter : IMultiValueConverter
    {
        private static readonly GapToBrushConverter Single = new GapToBrushConverter();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // values: [GapToPrevious, HighlightThresholdSeconds, ShowGapBorder]
            if (values != null && values.Length >= 3 && values[2] is bool sb && !sb)
            {
                return System.Windows.Media.Brushes.Transparent; // border cue toggled off
            }
            object gap = (values != null && values.Length > 0) ? values[0] : null;
            return Single.Convert(gap, targetType, parameter, culture);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps a row's <c>GapToPrevious</c> (TimeSpan?) to a compact human string: "+0.012s", "+3.4s",
    /// "+2m 5s", "+1h 3m", "+2d 4h". Null (first row) => "".
    /// </summary>
    public sealed class GapToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is TimeSpan g)) { return ""; }
            double s = g.TotalSeconds;
            if (s < 0) s = 0;

            if (s < 1) return "+" + g.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";
            if (s < 60) return "+" + g.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            if (s < 3600) return string.Format(CultureInfo.InvariantCulture, "+{0}m {1}s", (int)g.TotalMinutes, g.Seconds);
            if (s < 86400) return string.Format(CultureInfo.InvariantCulture, "+{0}h {1}m", (int)g.TotalHours, g.Minutes);
            return string.Format(CultureInfo.InvariantCulture, "+{0}d {1}h", (int)g.TotalDays, g.Hours);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Boolean =&gt; Visibility (true = Visible, false = Collapsed), for toggling the Δ column/border.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }
}
