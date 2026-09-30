using System;
using System.Windows;
using NuixLogReviewer.LogRepository;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// Small dialog for widening the current time range symmetrically: the user enters a duration that
    /// is subtracted from the start and added to the end. Used to "zoom out" from a narrow drill without
    /// having to guess a middle record and pivot. Mirrors <see cref="PivotTimeDialog"/>.
    /// </summary>
    public partial class ExpandRangeDialog : Window
    {
        public bool Success { get; private set; }

        /// <summary>The parsed amount added to EACH end when Success is true.</summary>
        public TimeSpan Amount { get; private set; }

        private readonly DateTime _currentMin;
        private readonly DateTime _currentMax;

        public ExpandRangeDialog(DateTime currentMin, DateTime currentMax, string defaultAmount = "10m")
        {
            InitializeComponent();
            Success = false;
            _currentMin = currentMin;
            _currentMax = currentMax;
            txtCurrent.Text = string.Format("{0:yyyy-MM-dd HH:mm:ss} \u2192 {1:yyyy-MM-dd HH:mm:ss}", currentMin, currentMax);
            txtAmount.Text = defaultAmount;
            UpdatePreview();
            txtAmount.TextChanged += (s, e) => UpdatePreview();
            Loaded += (s, e) => { txtAmount.Focus(); txtAmount.SelectAll(); };
        }

        /// <summary>Live-previews the resulting range as the user edits the amount.</summary>
        private void UpdatePreview()
        {
            if (txtPreview == null) return;
            if (TryComputeNewRange(out DateTime newMin, out DateTime newMax))
            {
                txtPreview.Text = string.Format("{0:yyyy-MM-dd HH:mm:ss} \u2192 {1:yyyy-MM-dd HH:mm:ss}", newMin, newMax);
                lblFeedback.Content = "";
            }
            else
            {
                txtPreview.Text = "\u2014";
            }
        }

        /// <summary>
        /// Computes the widened [min-amount, max+amount], clamping the low end at DateTime.MinValue and
        /// the high end at DateTime.MaxValue so an over-large amount can't overflow.
        /// </summary>
        private bool TryComputeNewRange(out DateTime newMin, out DateTime newMax)
        {
            newMin = _currentMin;
            newMax = _currentMax;
            string text = txtAmount.Text?.Trim();
            if (!CustomQueryParser.TryParseDuration(text, out TimeSpan amount) || amount <= TimeSpan.Zero)
            {
                return false;
            }

            // Clamp each side independently to avoid DateTime under/overflow on a huge amount.
            newMin = amount.Ticks >= _currentMin.Ticks ? DateTime.MinValue : _currentMin - amount;
            long room = DateTime.MaxValue.Ticks - _currentMax.Ticks;
            newMax = amount.Ticks >= room ? DateTime.MaxValue : _currentMax + amount;
            return true;
        }

        private void btnExpand_Click(object sender, RoutedEventArgs e)
        {
            string text = txtAmount.Text?.Trim();
            if (!CustomQueryParser.TryParseDuration(text, out TimeSpan amount) || amount <= TimeSpan.Zero)
            {
                lblFeedback.Content = "Enter a positive duration like 1h, 30m, 2d, or 90s.";
                return;
            }

            Amount = amount;
            Success = true;
            Close();
        }

        /// <summary>
        /// A quick-preset button: either a fixed duration ("dur:5m" -> set that amount) or a span
        /// multiplier ("mul:2" -> set the per-side amount so the TOTAL span grows by that factor, i.e.
        /// currentSpan*(N-1)/2 on each end). Fills the amount box (the preview updates live) rather than
        /// applying immediately, so the user can still tweak before hitting Expand.
        /// </summary>
        private void preset_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string tag)) return;

            if (tag.StartsWith("dur:", StringComparison.Ordinal))
            {
                txtAmount.Text = tag.Substring(4);
            }
            else if (tag.StartsWith("mul:", StringComparison.Ordinal)
                     && double.TryParse(tag.Substring(4), out double factor) && factor > 1)
            {
                TimeSpan span = _currentMax - _currentMin;
                // To grow the total span by `factor`, add (factor-1)/2 of the current span to each side.
                var perSide = TimeSpan.FromTicks((long)(span.Ticks * (factor - 1) / 2.0));
                if (perSide <= TimeSpan.Zero) { perSide = TimeSpan.FromSeconds(1); } // zero-width span guard
                txtAmount.Text = FormatDuration(perSide);
            }
            UpdatePreview();
        }

        /// <summary>Formats a TimeSpan back into a compact duration token the amount box accepts
        /// (e.g. "2d", "3h", "15m", "45s"), picking the coarsest whole unit; falls back to seconds.</summary>
        private static string FormatDuration(TimeSpan t)
        {
            if (t.Ticks <= 0) return "0s";
            if (t.TotalDays >= 1 && t.Ticks % TimeSpan.TicksPerDay == 0) return (long)t.TotalDays + "d";
            if (t.TotalHours >= 1 && t.Ticks % TimeSpan.TicksPerHour == 0) return (long)t.TotalHours + "h";
            if (t.TotalMinutes >= 1 && t.Ticks % TimeSpan.TicksPerMinute == 0) return (long)t.TotalMinutes + "m";
            // Otherwise express in whole seconds (rounded), which the parser accepts as a bare number.
            long secs = (long)Math.Round(t.TotalSeconds);
            if (secs < 1) secs = 1;
            return secs + "s";
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Success = false;
            Close();
        }
    }
}
