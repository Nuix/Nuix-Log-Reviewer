using System.ComponentModel;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Display item for the classifier table: a flag name, its hit count within the current filtered
    /// result set, and a per-session "shown" state driven by the eye toggle. When a classifier is
    /// hidden, its entries are filtered out of the current view (see MainWindow's hidden-flag handling).
    /// The initial IsShown is seeded from ClassifierVisibility.config defaults; the user can flip it for
    /// the session without changing the saved default.
    /// </summary>
    public class FlagCount : INotifyPropertyChanged
    {
        private bool _isShown = true;

        public string Name { get; set; }
        public int Count { get; set; }

        /// <summary>
        /// Human-readable description of the classifier's intent, shown in the Classifiers tab
        /// Description column. Sourced from the flag-&gt;description registry; blank when none is declared.
        /// </summary>
        public string Description { get; set; }

        /// <summary>Whether this classifier's entries are currently shown (true) or hidden (false).</summary>
        public bool IsShown
        {
            get { return _isShown; }
            set
            {
                if (_isShown == value) return;
                _isShown = value;
                OnPropertyChanged(nameof(IsShown));
                OnPropertyChanged(nameof(EyeGlyph));
                OnPropertyChanged(nameof(EyeToolTip));
            }
        }

        /// <summary>
        /// Eye glyph for the toggle button (Segoe MDL2 Assets): open eye when shown, "hide" eye when
        /// hidden. Monochrome symbol font so it always paints in the button's foreground brush (unlike
        /// color-emoji, which can render invisibly against the row background).
        /// </summary>
        public string EyeGlyph => _isShown ? "\uE7B3" : "\uED1A"; // RedEye vs Hide

        public string EyeToolTip => _isShown
            ? "Shown - click to hide these entries from the current view"
            : "Hidden - click to show these entries again";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
