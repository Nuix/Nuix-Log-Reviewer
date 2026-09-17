using System.Collections.Generic;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    /// <summary>A flag name paired with a human-readable description of what the flag means.</summary>
    public readonly struct ClassifierFlagDescription
    {
        public string Flag { get; }
        public string Description { get; }

        public ClassifierFlagDescription(string flag, string description)
        {
            Flag = flag;
            Description = description;
        }
    }

    /// <summary>
    /// Optional companion to <see cref="IEntryClassifier"/>. A compiled classifier that also implements
    /// this can declare a short description for each flag it emits; these surface in the Classifiers tab
    /// "Description" column so users understand the intent of each classifier. Implementing it is
    /// entirely optional - classifiers that don't simply show a blank description.
    /// (Scripted classifiers declare descriptions via script metadata instead; see
    /// <c>ScriptClassifierDefinition</c>.)
    /// </summary>
    public interface IClassifierDescriptions
    {
        /// <summary>Flag-name/description pairs for the flags this classifier can emit.</summary>
        IEnumerable<ClassifierFlagDescription> GetFlagDescriptions();
    }
}
