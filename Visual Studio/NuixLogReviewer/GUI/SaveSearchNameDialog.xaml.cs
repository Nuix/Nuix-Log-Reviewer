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
using System.Windows.Shapes;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// Interaction logic for SaveSearchDialog.xaml
    /// </summary>
    public partial class SaveSearchDialog : Window
    {
        public bool Success { get; protected set; }
        public string ProvidedName { get; protected set; }

        public SaveSearchDialog()
        {
            InitializeComponent();
            Success = false;
            ProvidedName = "";
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = txtName.Text;

            if (SavedSearchesRepo.NameIsInUse(name))
            {
                lblFeedback.Content = $"Sorry, the name '{name}' is already in use";
                return;
            }

            if(SavedSearchesRepo.NameIsValid(name) == false)
            {
                lblFeedback.Content = $"Sorry, the name contains invalid characters only these are allowd:\nA-Z a-z 0-9 - _ ()";
                return;
            }

            Success = true;
            ProvidedName = name.Trim();

            this.Close();
        }
    }
}
