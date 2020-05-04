using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Represents a value with a name associated.  Used to supply paramter values to SQL code.
    /// TODO: look at replacing this with built in KeyValuePair type.
    /// </summary>
    public class NamedValue
    {
        public string Name { get; set; }
        public object Value { get; set; }

        public NamedValue() { }
        public NamedValue(string name, object value)
        {
            Name = name;
            Value = value;
        }
    }
}
