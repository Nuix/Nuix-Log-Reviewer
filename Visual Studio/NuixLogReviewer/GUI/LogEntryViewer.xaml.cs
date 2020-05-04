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
        public LogEntryViewer()
        {
            InitializeComponent();
            checkWrapText.Checked += checkWrapText_Checked;
            checkWrapText.Unchecked += checkWrapText_Checked;
        }

        public void Clear()
        {
            txtTimeStamp.Text = "";
            txtElapsed.Text = "";
            txtLevel.Text = "";
            txtLineNumber.Text = "";
            txtSource.Text = "";
            txtChannel.Text = "";
            txtContent.Text = "";
            txtFilePath.Text = "";
            flagList.ItemsSource = new String[] { };

        }

        public void SetLogEntry(NuixLogEntry entry)
        {
            if (entry == null)
            {
                Clear();
            }
            else
            {
                txtTimeStamp.Text = entry.TimeStamp.ToString();
                txtElapsed.Text = entry.Elapsed.ToString();
                txtLevel.Text = entry.Level;
                txtLineNumber.Text = entry.LineNumber.ToString();
                txtSource.Text = entry.Source;
                txtChannel.Text = entry.Channel;
                txtContent.Text = entry.Content;
                txtFilePath.Text = entry.FilePath;
                flagList.ItemsSource = entry.Flags;
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
