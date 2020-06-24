using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NuixLogReviewer
{
    /// <summary>
    /// Manages some core details regarding saved searches:
    /// - Retrieve whats available
    /// - Validate names
    /// - Saving a search
    /// </summary>
    public class SavedSearchesRepo
    {
        private static string appDir = null;
        private static string savedSearchDir = null;
        private static Regex invalidNameChars = new Regex(@"[^a-zA-Z0-9 _\-\(\)]+", RegexOptions.Compiled);

        static SavedSearchesRepo()
        {
            appDir = System.IO.Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            savedSearchDir = Path.Combine(appDir, "SavedSearches");
        }

        /// <summary>
        /// Saves a search, which is persisted by just a text file.
        /// </summary>
        /// <param name="name">Name of the saved search (also the name of the created text file)</param>
        /// <param name="query">Query to save, saved as the content of the created text file</param>
        public static void SaveSearch(string name, string query)
        {
            string outputFile = Path.Combine(savedSearchDir, $"{name}.txt");
            File.WriteAllText(outputFile, query);
        }

        /// <summary>
        /// Gets a listing of available saved searches by looking for *.txt in saved searches directory.
        /// </summary>
        /// <returns>List of available saved searches.</returns>
        public static List<SavedSearch> GetSavedSearches()
        {
            List<SavedSearch> result = new List<SavedSearch>();
            var savedSearchFiles = Directory.GetFiles(savedSearchDir, "*.txt");
            foreach (var savedSearchFile in savedSearchFiles)
            {
                string savedSearchName = Path.GetFileNameWithoutExtension(savedSearchFile);
                string query = File.ReadAllText(savedSearchFile).Trim();
                result.Add(new SavedSearch(savedSearchName, query));
            }
            result = result.OrderBy(s => s.Name).ToList();
            return result;
        }

        /// <summary>
        /// Returns true is the provided name conflicts with the name of an already existing saved search (case insensitive).
        /// </summary>
        /// <param name="name">The name to check</param>
        /// <returns>True if the provided name is in use already</returns>
        public static bool NameIsInUse(string name)
        {
            var savedSearchFiles = Directory.GetFiles(savedSearchDir, "*.txt");
            foreach (var savedSearchFile in savedSearchFiles)
            {
                string savedSearchName = Path.GetFileNameWithoutExtension(savedSearchFile);
                if (name.Trim().ToLower() == savedSearchName.Trim().ToLower())
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines whether a given name has characters we don't accept.
        /// </summary>
        /// <param name="name">The name to check</param>
        /// <returns>True if name contains only characters which we allow</returns>
        public static bool NameIsValid(string name)
        {
            return !invalidNameChars.IsMatch(name);
        }
    }
}
