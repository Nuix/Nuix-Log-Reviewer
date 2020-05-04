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
    /// Interaction logic for LogEntryGrid.xaml
    /// </summary>
    public partial class LogEntryGrid : UserControl
    {
        public IList<NuixLogEntry> CurrentLogEntries
        {
            get; private set;
        }

        public LogEntryGrid()
        {
            InitializeComponent();
        }

        public void SetLogEntries(IList<NuixLogEntry> entries)
        {
            CurrentLogEntries = entries;
            resultsGrid.ItemsSource = entries;
        }

        public event SelectedCellsChangedEventHandler SelectedCellsChanged
        {
            add { resultsGrid.SelectedCellsChanged += value; }
            remove { resultsGrid.SelectedCellsChanged -= value; }
        }

        // Events
        public delegate void SelectedLogEntryChangedDel(NuixLogEntry selectedEntry);
        public event SelectedLogEntryChangedDel SelectedLogEntryChanged;

        private void resultsGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            if (e.AddedCells != null && e.AddedCells.Count > 0)
            {
                var entry = e.AddedCells.First().Item as NuixLogEntry;
                SelectedLogEntryChanged(entry);
            }
        }
    }
}
