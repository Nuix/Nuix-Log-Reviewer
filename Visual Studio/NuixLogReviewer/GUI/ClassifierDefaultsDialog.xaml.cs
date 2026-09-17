using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace NuixLogReviewer.GUI
{
    /// <summary>
    /// Editor for the per-classifier default visibility (ClassifierVisibility.config). Presents each
    /// known flag with a "shown by default" checkbox; Save persists the choices (a flag is written as
    /// "hidden" when unchecked, omitted/shown otherwise). Defaults take effect on the next load.
    /// </summary>
    public partial class ClassifierDefaultsDialog : Window
    {
        /// <summary>Row model: a flag and whether it is shown by default (checkbox bound TwoWay).</summary>
        public sealed class FlagDefault : INotifyPropertyChanged
        {
            public string Name { get; set; }
            private bool _shown = true;
            public bool Shown
            {
                get => _shown;
                set { if (_shown != value) { _shown = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shown))); } }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<FlagDefault> _items;

        /// <summary>True if the user saved changes (so the caller can react).</summary>
        public bool Saved { get; private set; }

        /// <summary>
        /// Builds the dialog from the union of the given (currently-loaded) flags and any flags already
        /// present in the config, so a preference can be set even for flags not in the current data.
        /// </summary>
        public ClassifierDefaultsDialog(IEnumerable<string> loadedFlags)
        {
            InitializeComponent();

            var defaults = ClassifierVisibilityRepo.Load(); // flag -> hiddenByDefault
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (loadedFlags != null)
            {
                foreach (var f in loadedFlags)
                    if (!string.IsNullOrWhiteSpace(f)) names.Add(f.Trim().ToLowerInvariant());
            }
            foreach (var f in defaults.Keys) names.Add(f);

            _items = names.Select(n => new FlagDefault
            {
                Name = n,
                Shown = !(defaults.TryGetValue(n, out bool hidden) && hidden),
            }).ToList();

            flagItems.ItemsSource = _items;
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Persist hidden = !shown for every known flag.
                var map = _items.ToDictionary(i => i.Name, i => !i.Shown, StringComparer.OrdinalIgnoreCase);
                ClassifierVisibilityRepo.Save(map);
                Saved = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                lblFeedback.Content = "Save failed: " + ex.Message;
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
