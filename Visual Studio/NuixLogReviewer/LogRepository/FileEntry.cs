using System.ComponentModel;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Display item for the Files tab: a source file (identified by its full path), its entry count
    /// within the current filtered set, a disambiguated short display name (with the full path as a
    /// tooltip), and a per-session "shown" state driven by the eye toggle. Hiding a file filters its
    /// entries out of the current view (see MainWindow's hidden-file handling). Mirrors
    /// <see cref="FlagCount"/> so the Files tab can reuse the classifier-visibility UI patterns.
    /// </summary>
    public class FileEntry : INotifyPropertyChanged
    {
        private bool _isShown = true;

        /// <summary>Full path of the source log file (the identity; lower-cased to match the index key).</summary>
        public string Path { get; set; }

        /// <summary>Short, disambiguated display name (shortest unique tail of the path).</summary>
        public string DisplayName { get; set; }

        /// <summary>Entry count for this file within the current filtered set.</summary>
        public int Count { get; set; }

        /// <summary>Whether this file's entries are currently shown (true) or hidden (false).</summary>
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

        /// <summary>Eye glyph (Segoe MDL2 Assets): RedEye when shown, Hide when hidden.</summary>
        public string EyeGlyph => _isShown ? "\uE7B3" : "\uED1A";

        public string EyeToolTip => _isShown
            ? "Shown - click to hide this file's entries from the current view"
            : "Hidden - click to show this file's entries again";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
