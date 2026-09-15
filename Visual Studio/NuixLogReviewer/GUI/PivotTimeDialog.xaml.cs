using System;
using System.Windows;
using NuixLogReviewer.LogRepository;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// Small dialog for choosing a +/- time window to pivot around a selected log entry's event time.
    /// </summary>
    public partial class PivotTimeDialog : Window
    {
        public bool Success { get; private set; }

        /// <summary>The parsed window (applied on each side of the event) when Success is true.</summary>
        public TimeSpan Window { get; private set; }

        public PivotTimeDialog(DateTime eventTime, string defaultWindow = "1h")
        {
            InitializeComponent();
            Success = false;
            txtEventTime.Text = eventTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
            txtWindow.Text = defaultWindow;
            Loaded += (s, e) => { txtWindow.Focus(); txtWindow.SelectAll(); };
        }

        private void btnPivot_Click(object sender, RoutedEventArgs e)
        {
            string text = txtWindow.Text?.Trim();
            if (!CustomQueryParser.TryParseDuration(text, out TimeSpan window) || window <= TimeSpan.Zero)
            {
                lblFeedback.Content = "Enter a positive duration like 1h, 30m, 2d, or 90s.";
                return;
            }

            Window = window;
            Success = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Success = false;
            Close();
        }
    }
}
