using System;
using System.Collections.Generic;
using System.Linq;

namespace NuixLogReviewer
{
    /// <summary>
    /// An immutable snapshot of "what the user is looking at": the base query plus the session
    /// view-filters (hidden classifiers / files / patterns) and a position anchor (an entry id to
    /// re-select and scroll to). This is the unit that Forward/Back history stores and restores, and is
    /// deliberately a standalone, serializable-shaped type so BOOKMARKS (and later scripted bookmark
    /// seeding) can reuse it without change.
    /// </summary>
    public sealed class ViewState
    {
        public string BaseQuery { get; }
        public IReadOnlyList<string> HiddenFlags { get; }
        public IReadOnlyList<string> HiddenFiles { get; }
        public IReadOnlyList<string> HiddenTemplates { get; }
        /// <summary>Entry id to re-select/scroll to on restore (the selected row, else the top-visible row), or null.</summary>
        public long? PositionEntryId { get; }

        public ViewState(string baseQuery,
                         IEnumerable<string> hiddenFlags,
                         IEnumerable<string> hiddenFiles,
                         IEnumerable<string> hiddenTemplates,
                         long? positionEntryId)
        {
            BaseQuery = baseQuery ?? "";
            HiddenFlags = (hiddenFlags ?? Enumerable.Empty<string>()).ToList();
            HiddenFiles = (hiddenFiles ?? Enumerable.Empty<string>()).ToList();
            HiddenTemplates = (hiddenTemplates ?? Enumerable.Empty<string>()).ToList();
            PositionEntryId = positionEntryId;
        }

        /// <summary>
        /// Value-equality on the navigational content (query + hidden-sets), IGNORING position, so
        /// history doesn't push a new step when only the scroll/selection moved. Hidden-sets compared
        /// order-insensitively (case-insensitive).
        /// </summary>
        public bool SameView(ViewState other)
        {
            if (other == null) return false;
            return string.Equals(BaseQuery ?? "", other.BaseQuery ?? "", StringComparison.Ordinal)
                && SetEq(HiddenFlags, other.HiddenFlags)
                && SetEq(HiddenFiles, other.HiddenFiles)
                && SetEq(HiddenTemplates, other.HiddenTemplates);
        }

        private static bool SetEq(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            return b.All(sa.Contains);
        }
    }

    /// <summary>
    /// A browser-style Back/Forward stack of <see cref="ViewState"/>s. A single "current" pointer moves
    /// within a list; pushing a new state after going Back truncates the forward branch (like a browser).
    /// Pushing a state that <see cref="ViewState.SameView"/> as the current one only updates its position
    /// (no new history entry), so re-running the same view or moving the selection doesn't spam history.
    /// </summary>
    public sealed class NavigationHistory
    {
        private readonly List<ViewState> _stack = new List<ViewState>();
        private int _index = -1;          // pointer to the current state in _stack, or -1 when empty
        private readonly int _capacity;

        public NavigationHistory(int capacity = 200)
        {
            _capacity = Math.Max(2, capacity);
        }

        public bool CanBack => _index > 0;
        public bool CanForward => _index >= 0 && _index < _stack.Count - 1;
        public ViewState Current => (_index >= 0 && _index < _stack.Count) ? _stack[_index] : null;
        public int Count => _stack.Count;

        /// <summary>Empties the history (e.g. when a new log set is loaded into the same session).</summary>
        public void Clear()
        {
            _stack.Clear();
            _index = -1;
        }

        /// <summary>
        /// Records a navigation to <paramref name="state"/>. If it's the same view as the current entry
        /// (query+hidden-sets), just refreshes that entry's stored position instead of adding a step.
        /// Otherwise truncates any forward branch, appends, advances the pointer, and trims to capacity.
        /// </summary>
        public void Push(ViewState state)
        {
            if (state == null) return;

            if (Current != null && Current.SameView(state))
            {
                // Same view - update the position anchor in place, don't create a new history entry.
                _stack[_index] = state;
                return;
            }

            // Truncate the forward branch (anything after the current pointer).
            if (_index < _stack.Count - 1)
            {
                _stack.RemoveRange(_index + 1, _stack.Count - _index - 1);
            }

            _stack.Add(state);
            _index = _stack.Count - 1;

            // Trim oldest entries beyond capacity.
            if (_stack.Count > _capacity)
            {
                int remove = _stack.Count - _capacity;
                _stack.RemoveRange(0, remove);
                _index -= remove;
            }
        }

        /// <summary>Moves the pointer back one and returns that state, or null if not possible.</summary>
        public ViewState Back()
        {
            if (!CanBack) return null;
            _index--;
            return _stack[_index];
        }

        /// <summary>Moves the pointer forward one and returns that state, or null if not possible.</summary>
        public ViewState Forward()
        {
            if (!CanForward) return null;
            _index++;
            return _stack[_index];
        }

        /// <summary>
        /// Updates the current entry's position anchor without changing history (e.g. as the user
        /// scrolls/selects after landing on a view). No-op when empty.
        /// </summary>
        public void UpdateCurrentPosition(ViewState stateWithPosition)
        {
            if (_index >= 0 && stateWithPosition != null && Current.SameView(stateWithPosition))
            {
                _stack[_index] = stateWithPosition;
            }
        }
    }
}
