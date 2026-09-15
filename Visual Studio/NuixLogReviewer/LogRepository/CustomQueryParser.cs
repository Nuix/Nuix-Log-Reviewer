using Lucene.Net.Analysis;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NuixLogReviewer.LogRepository
{
    /// <summary>
    /// Classic QueryParser (Lucene.NET 4.8) with two extensions:
    /// <list type="bullet">
    /// <item>Fields listed in <see cref="LongFields"/> are treated as numeric (Int64) so term and
    /// range queries against them use NumericRangeQuery instead of text.</item>
    /// <item>The virtual "age" field lets users query how long before the reference time an event
    /// occurred, e.g. "age:24h" (age &lt;= 24 hours) or "age:[1h TO 3d]". Age terms are rewritten
    /// into NumericRangeQuery over the real "timestamp" (ticks) field. See <see cref="AgeField"/>.</item>
    /// </list>
    /// </summary>
    public class CustomQueryParser : QueryParser
    {
        /// <summary>Name of the virtual age field users type in queries.</summary>
        public const string AgeField = "age";

        /// <summary>Name of the real indexed field (event time in ticks) that age maps onto.</summary>
        public const string TimestampField = "timestamp";

        public List<string> LongFields { get; set; }
        public List<int> IntFields { get; set; }

        /// <summary>
        /// Reference time (in <see cref="DateTime.Ticks"/>) that "age" is measured back from -
        /// typically the newest event in the loaded set. When null, age queries match nothing.
        /// </summary>
        public long? AgeReferenceTicks { get; set; }

        private readonly Regex digitsOnly = new Regex("^[0-9]+$", RegexOptions.Compiled);

        // A duration like "24h", "30m", "1d", "90s", or a bare number (interpreted as seconds).
        private static readonly Regex DurationRegex = new Regex(
            @"^\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>[smhd]?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public CustomQueryParser(LuceneVersion version, Analyzer analyzer)
            : base(version, "content", analyzer)
        {
            LongFields = new List<string>();
            IntFields = new List<int>();
        }

        protected override Query GetFieldQuery(string field, string queryText, bool quoted)
        {
            if (IsAgeField(field))
            {
                // A single age term means "age <= value", i.e. events from (reference - value) to reference.
                return BuildAgeRangeQuery("0", queryText);
            }

            if (LongFields.Contains(field, StringComparer.OrdinalIgnoreCase) && digitsOnly.IsMatch(queryText))
            {
                return GetRangeQuery(field, queryText, queryText, true, true);
            }
            return base.GetFieldQuery(field, queryText, quoted);
        }

        protected override Query GetRangeQuery(string field, string part1, string part2, bool startInclusive, bool endInclusive)
        {
            if (IsAgeField(field))
            {
                return BuildAgeRangeQuery(part1, part2);
            }

            if (LongFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                long.TryParse(part1, out long v1);
                long.TryParse(part2, out long v2);

                return NumericRangeQuery.NewInt64Range(field, v1, v2, startInclusive, endInclusive);
            }

            return base.GetRangeQuery(field, part1, part2, startInclusive, endInclusive);
        }

        private static bool IsAgeField(string field) =>
            string.Equals(field, AgeField, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Builds a timestamp range query from an age range. An event's age is
        /// (reference - event.Ticks), so an age in [loAge, hiAge] corresponds to a timestamp in
        /// [reference - hiAge, reference - loAge]. Range bounds "*" (or unparseable) are treated as
        /// open: a missing low age => 0, a missing high age => +infinity (capped at reference).
        /// </summary>
        private Query BuildAgeRangeQuery(string loAgePart, string hiAgePart)
        {
            if (AgeReferenceTicks == null)
            {
                // No reference established (no logs loaded) - match nothing rather than throw.
                return new BooleanQuery();
            }

            long reference = AgeReferenceTicks.Value;

            long loAgeTicks = ParseDurationTicks(loAgePart, defaultIfOpen: 0);
            long hiAgeTicks = ParseDurationTicks(hiAgePart, defaultIfOpen: long.MaxValue);

            // timestamp low  = reference - hiAge  (clamp so we don't underflow below 0)
            // timestamp high = reference - loAge
            long tsLow = hiAgeTicks == long.MaxValue ? long.MinValue : SafeSub(reference, hiAgeTicks);
            long tsHigh = SafeSub(reference, loAgeTicks);

            return NumericRangeQuery.NewInt64Range(TimestampField, tsLow, tsHigh, true, true);
        }

        /// <summary>
        /// Parses a duration token (e.g. "24h", "30m", "90s", "1d", or bare seconds) into ticks.
        /// Open-range markers ("*", empty, null) return <paramref name="defaultIfOpen"/>.
        /// </summary>
        private static long ParseDurationTicks(string token, long defaultIfOpen)
        {
            if (string.IsNullOrWhiteSpace(token) || token == "*")
            {
                return defaultIfOpen;
            }

            return TryParseDuration(token, out TimeSpan span) ? span.Ticks : defaultIfOpen;
        }

        /// <summary>
        /// Parses a duration string like "24h", "30m", "90s", "1d", or a bare number (seconds) into
        /// a <see cref="TimeSpan"/>. Returns false if the string isn't a recognized duration.
        /// Shared by the age query translation and the "pivot around event" dialog so both accept
        /// the same syntax.
        /// </summary>
        public static bool TryParseDuration(string token, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var m = DurationRegex.Match(token);
            if (!m.Success)
            {
                return false;
            }

            double value = double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
            string unit = m.Groups["unit"].Value.ToLowerInvariant();

            switch (unit)
            {
                case "d": duration = TimeSpan.FromDays(value); break;
                case "h": duration = TimeSpan.FromHours(value); break;
                case "m": duration = TimeSpan.FromMinutes(value); break;
                case "s":
                case "":
                default: duration = TimeSpan.FromSeconds(value); break;
            }
            return true;
        }

        private static long SafeSub(long a, long b)
        {
            // Avoid overflow when b is huge; clamp to long.MinValue.
            try { return checked(a - b); }
            catch (OverflowException) { return long.MinValue; }
        }
    }
}
