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
    /// Interaction logic for LogEntryViewer.xaml
    /// </summary>
    public partial class LogEntryViewer : UserControl
    {
        // The entry currently shown, so the Copy Log Line button can reconstruct its full line.
        private NuixLogEntry _current;

        public LogEntryViewer()
        {
            InitializeComponent();
            checkWrapText.Checked += checkWrapText_Checked;
            checkWrapText.Unchecked += checkWrapText_Checked;
        }

        public void Clear()
        {
            _current = null;
            txtTimeStamp.Text = "";
            txtElapsed.Text = "";
            txtLevel.Text = "";
            txtLineNumber.Text = "";
            txtSource.Text = "";
            txtChannel.Text = "";
            txtContent.Text = "";
            txtFilePath.Text = "";
            flagList.ItemsSource = new object[] { };
            if (btnCopyLogLine != null) { btnCopyLogLine.IsEnabled = false; }
        }

        /// <summary>Item shown in the Assigned Flags list: the flag name plus its description tooltip.</summary>
        private sealed class FlagView
        {
            public string Name { get; set; }
            public string Description { get; set; }
        }

        /// <summary>
        /// Projects an entry's raw flag strings into display items, attaching each flag's description
        /// (from the classifier description registry) as a tooltip. Flags without a declared description
        /// simply have no tooltip.
        /// </summary>
        private static IEnumerable<FlagView> ToFlagViews(IEnumerable<string> flags)
        {
            if (flags == null) { return new FlagView[] { }; }
            return flags.Select(f => new FlagView
            {
                Name = f,
                Description = LogRepository.Classifiers.ClassifierDescriptionRegistry.DescriptionFor(f),
            }).ToList();
        }

        public void SetLogEntry(NuixLogEntry entry)
        {
            _current = entry;
            if (entry == null)
            {
                Clear();
            }
            else
            {
                txtTimeStamp.Text = entry.TimeStamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                txtElapsed.Text = entry.Elapsed.ToString();
                txtLevel.Text = entry.Level;
                txtLineNumber.Text = entry.LineNumber.ToString();
                txtSource.Text = entry.Source;
                txtChannel.Text = entry.Channel;
                txtContent.Text = entry.Content;
                txtFilePath.Text = entry.FilePath;
                flagList.ItemsSource = ToFlagViews(entry.Flags);
                btnCopyLogLine.IsEnabled = true;
            }
        }

        /// <summary>Copies the full reconstructed log line for the shown entry to the clipboard.</summary>
        private void btnCopyLogLine_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) { return; }
            try
            {
                Clipboard.SetText(_current.ToLogLine());
            }
            catch (Exception)
            {
                // Clipboard can be transiently locked by another process; ignore rather than crash.
            }
        }

        private void checkWrapText_Checked(object sender, RoutedEventArgs e)
        {
            if (checkWrapText.IsChecked.HasValue && checkWrapText.IsChecked.Value == true)
            {
                txtContent.TextWrapping = TextWrapping.Wrap;
            } else if (checkWrapText.IsChecked.HasValue && checkWrapText.IsChecked.Value == false)
            {
                txtContent.TextWrapping = TextWrapping.NoWrap;
            }
        }
    }
}
