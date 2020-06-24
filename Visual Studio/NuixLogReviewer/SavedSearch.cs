using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer
{
    /// <summary>
    /// Basic container class to represent a saved search.
    /// </summary>
    public class SavedSearch
    {
        public string Name { get; set; }
        public string Query { get; set; }

        public SavedSearch(string name, string query)
        {
            this.Name = name;
            this.Query = query;
        }
    }
}
