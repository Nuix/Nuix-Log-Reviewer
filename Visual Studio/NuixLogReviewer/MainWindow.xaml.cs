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
        /// When a user selects a given row in the log entry grid, display that entry's details
        /// in the leg entry viewer.
        /// </summary>
        /// <param name="selectedEntry">The log entry which was selected.</param>
        private void resultsGrid_SelectedLogEntryChanged(NuixLogEntry selectedEntry)
        {
            logEntryViewer.SetLogEntry(selectedEntry);
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
        /// Allows a user to select a directory.  All files matching "nuix*.log*" are located and
        /// then loaded using loadLogFiles method.
        /// </summary>
        private void menuLoadDirectory_Click(object sender, RoutedEventArgs e)
        {
            VistaFolderBrowserDialog dialog = new VistaFolderBrowserDialog();
            if (dialog.ShowDialog() == true)
            {
                string selectedDirectory = dialog.SelectedPath;
                string[] logFiles = System.IO.Directory.GetFiles(selectedDirectory, "nuix*.log*", System.IO.SearchOption.AllDirectories);

                // Dispose of current repo
                NuixLogRepo prev = repo;
                repo = new NuixLogRepo();
                prev.DisposeRepo();

                loadLogFiles(logFiles);
            }
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
                        flagList.ItemsSource = repo.Database.GetAllFlags();
                        // IsBusy will be cleared by performSearch()
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
        private void performSearch()
        {
            IsBusy = true;
            string query = txtSearchQuery.Text;
            logEntryViewer.Clear();
            lblStatus.Text = "Executing search:\n" + query;
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
                    LogEntrySearchResponse hits = repo.Search(query);
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

                        resultsGrid.SetLogEntries(hits);
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
        /// When user double clicks a flag from the list, we append it to the search bar.
        /// </summary>
        private void flagList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (flagList.SelectedIndex > -1)
            {
                string flag = (string)flagList.Items[flagList.SelectedIndex];
                drillDownSearch("flag:" + flag, false);
            }
        }

        /// <summary>
        /// Takes the query in the search bar (if there is one) and augments it with additional criteria
        /// and then runs the new serach.
        /// </summary>
        /// <param name="additionalCriteria">Additional criteria to add to the query.  Will be ANDed to value in search bar if there is one.</param>
        private void drillDownSearch(string additionalCriteria, bool executeNewQuery = true)
        {
            string existingQuery = txtSearchQuery.Text;
            if (String.IsNullOrWhiteSpace(existingQuery))
            {
                txtSearchQuery.Text = additionalCriteria;
                if (executeNewQuery) { performSearch(); }
            }
            else
            {
                txtSearchQuery.Text = String.Format("({0}) AND {1}", existingQuery, additionalCriteria);
                if (executeNewQuery) { performSearch(); }
            }
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
