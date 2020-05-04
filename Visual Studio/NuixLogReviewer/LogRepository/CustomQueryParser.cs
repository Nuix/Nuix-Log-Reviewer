using Lucene.Net.Analysis;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class CustomQueryParser : QueryParser
    {
        public List<string> LongFields { get; set; }
        public List<int> IntFields { get; set; }

        private Regex digitsOnly = new Regex("^[0-9]+$", RegexOptions.Compiled);

        public CustomQueryParser(Analyzer a)
            : base(Lucene.Net.Util.Version.LUCENE_30, "content", a)
        {
            LongFields = new List<string>();
            IntFields = new List<int>();
        }

        protected override Query GetFieldQuery(string field, string queryText, int slop)
        {
            if (LongFields.Contains(field, StringComparer.OrdinalIgnoreCase) && digitsOnly.Match(queryText).Success)
            {
                return GetRangeQuery(field, queryText, queryText, true);
            }
            return base.GetFieldQuery(field, queryText, slop);
        }

        protected override Lucene.Net.Search.Query GetRangeQuery(string field, string part1, string part2, bool inclusive)
        {
            if (LongFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                long v1;
                long v2;

                long.TryParse(part1, out v1);
                long.TryParse(part2, out v2);

                return NumericRangeQuery.NewLongRange(field, v1, v2, inclusive, inclusive);
            }
            else
                return base.GetRangeQuery(field, part1, part2, inclusive);
        }
    }
}
