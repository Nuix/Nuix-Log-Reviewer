using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NuixLogReviewer.Reporting
{
    /// <summary>
    /// Builds the interactive-timeline data + analytics for the pattern-summary HTML report, and emits
    /// the self-contained chart markup (uPlot inlined). Kept free of WPF/DB types so the binning and
    /// lead-lag math can be unit/harness tested directly.
    ///
    /// Input per pattern: a label, a color, and the sorted event timestamps (UTC ticks). Output: a shared
    /// time grid (bin edges) with per-pattern counts, plus lightweight analytics (peak/burst window,
    /// onset, and pairwise lead-lag correlation) that reinforce the "A spikes when B increases" story.
    /// </summary>
    public static class PatternReportChart
    {
        public sealed class SeriesInput
        {
            public string Id;        // short level-aware identifier, e.g. "E1" (used everywhere for reference)
            public string Template;  // full pattern template (shown once in the key table)
            public string Color;
            public IReadOnlyList<long> EventTicks; // sorted ascending
        }

        public sealed class SeriesAnalytics
        {
            public string Id;
            public string Template;
            public string Color;
            public long Count;
            public long OnsetMs;        // first event (ms since unix epoch), = onset marker
            public long PeakBinMs;      // left edge of the bin with the most events
            public long PeakBinCount;   // events in that bin
            public double BurstSharePct; // % of this series' events inside the single densest window (== peak bin)
        }

        public sealed class LeadLag
        {
            public string A;        // series id
            public string B;        // series id
            public double R;        // best Pearson correlation of A vs B over tested lags
            public long LagMs;      // +ve => A leads B by LagMs; -ve => B leads A
        }

        public sealed class TimelineModel
        {
            public long BinMs;                 // chosen bin width in ms
            public long StartMs;               // left edge of first bin (ms since unix epoch)
            public int BinCount;
            public long[] BinEdgesMs;          // length BinCount (left edges)
            public List<string> Ids = new List<string>();
            public List<string> Templates = new List<string>();
            public List<string> Colors = new List<string>();
            public List<long[]> Counts = new List<long[]>();       // per series, length BinCount
            public List<SeriesAnalytics> Analytics = new List<SeriesAnalytics>();
            public List<LeadLag> LeadLags = new List<LeadLag>();
            public bool HasData;
        }

        private const long TicksPerMs = TimeSpan.TicksPerMillisecond;
        // .NET ticks at unix epoch (1970-01-01Z), for converting ticks -> ms-since-epoch for JS Date.
        private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static long TicksToUnixMs(long ticks) => (ticks - UnixEpochTicks) / TicksPerMs;

        /// <summary>
        /// Builds the timeline model. <paramref name="targetBins"/> is the desired resolution across the
        /// full span (bin width is derived from it and rounded to a "nice" duration). Returns a model with
        /// HasData=false when there are fewer than 2 total events or no time span.
        /// </summary>
        public static TimelineModel Build(IReadOnlyList<SeriesInput> series, int targetBins = 200)
        {
            var model = new TimelineModel();
            if (series == null || series.Count == 0) return model;

            long min = long.MaxValue, max = long.MinValue, total = 0;
            foreach (var s in series)
            {
                if (s.EventTicks == null || s.EventTicks.Count == 0) continue;
                total += s.EventTicks.Count;
                if (s.EventTicks[0] < min) min = s.EventTicks[0];
                if (s.EventTicks[s.EventTicks.Count - 1] > max) max = s.EventTicks[s.EventTicks.Count - 1];
            }
            if (total < 2 || min == long.MaxValue || max <= min) return model;

            long spanMs = Math.Max(1, (max - min) / TicksPerMs);
            if (targetBins < 1) targetBins = 1;
            long binMs = NiceBin(Math.Max(1, spanMs / targetBins));
            long startMs = TicksToUnixMs(min);
            // Align the start to a multiple of the bin so edges are stable/round.
            startMs = (startMs / binMs) * binMs;
            long endMs = TicksToUnixMs(max);
            int binCount = (int)((endMs - startMs) / binMs) + 1;
            if (binCount < 1) binCount = 1;

            model.BinMs = binMs;
            model.StartMs = startMs;
            model.BinCount = binCount;
            model.BinEdgesMs = new long[binCount];
            for (int i = 0; i < binCount; i++) model.BinEdgesMs[i] = startMs + (long)i * binMs;

            foreach (var s in series)
            {
                var counts = new long[binCount];
                if (s.EventTicks != null)
                {
                    foreach (var t in s.EventTicks)
                    {
                        long ms = TicksToUnixMs(t);
                        int b = (int)((ms - startMs) / binMs);
                        if (b < 0) b = 0; else if (b >= binCount) b = binCount - 1;
                        counts[b]++;
                    }
                }
                model.Ids.Add(s.Id);
                model.Templates.Add(s.Template);
                model.Colors.Add(s.Color);
                model.Counts.Add(counts);

                // Analytics: onset, peak bin, burst share (peak bin's share of the series total).
                long cnt = s.EventTicks?.Count ?? 0;
                int peakIdx = 0; long peak = 0;
                for (int i = 0; i < binCount; i++) if (counts[i] > peak) { peak = counts[i]; peakIdx = i; }
                model.Analytics.Add(new SeriesAnalytics
                {
                    Id = s.Id,
                    Template = s.Template,
                    Color = s.Color,
                    Count = cnt,
                    OnsetMs = (s.EventTicks != null && s.EventTicks.Count > 0) ? TicksToUnixMs(s.EventTicks[0]) : startMs,
                    PeakBinMs = startMs + (long)peakIdx * binMs,
                    PeakBinCount = peak,
                    BurstSharePct = cnt > 0 ? 100.0 * peak / cnt : 0,
                });
            }

            model.LeadLags = ComputeLeadLags(model);
            model.HasData = true;
            return model;
        }

        /// <summary>
        /// Rounds a raw bin width (ms) up to a human-friendly duration: 1/2/5 x 10^n within sub-second,
        /// then common second/minute/hour/day steps. Keeps axis ticks and tooltips readable.
        /// </summary>
        internal static long NiceBin(long ms)
        {
            long[] niceMs =
            {
                1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500,
                1000, 2000, 5000, 10000, 15000, 30000,               // seconds
                60000, 120000, 300000, 600000, 900000, 1800000,      // minutes
                3600000, 7200000, 10800000, 21600000, 43200000,      // hours
                86400000, 172800000, 604800000                       // days / week
            };
            foreach (var n in niceMs) if (n >= ms) return n;
            return niceMs[niceMs.Length - 1];
        }

        /// <summary>
        /// Pairwise lead-lag: for each pair of series, find the integer bin lag (within +/- a bounded
        /// window) maximizing the Pearson correlation of their per-bin count vectors. Reports only pairs
        /// with a meaningfully positive correlation. O(pairs * lags * bins) over already-small bin arrays.
        /// </summary>
        private static List<LeadLag> ComputeLeadLags(TimelineModel m)
        {
            var result = new List<LeadLag>();
            int n = m.Counts.Count;
            if (n < 2 || m.BinCount < 4) return result;

            int maxLag = Math.Min(20, m.BinCount / 2); // don't test lags beyond half the series
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double bestR = 0; int bestLag = 0;
                    for (int lag = -maxLag; lag <= maxLag; lag++)
                    {
                        double r = Pearson(m.Counts[i], m.Counts[j], lag);
                        if (r > bestR) { bestR = r; bestLag = lag; }
                    }
                    if (bestR >= 0.5) // only surface a real relationship
                    {
                        // Positive lag here means series i's bins line up with series j's bins shifted
                        // later, i.e. i LEADS j. Express A leads B by LagMs (>0) accordingly.
                        result.Add(new LeadLag
                        {
                            A = m.Ids[i],
                            B = m.Ids[j],
                            R = Math.Round(bestR, 2),
                            LagMs = bestLag * m.BinMs,
                        });
                    }
                }
            }
            // Strongest relationships first.
            result.Sort((a, b) => b.R.CompareTo(a.R));
            return result;
        }

        /// <summary>
        /// Pearson correlation of x vs y where y is shifted by <paramref name="lag"/> bins (y[k+lag]).
        /// Positive lag =&gt; x leads y. Returns 0 when the overlap is too small or a series is flat.
        /// </summary>
        internal static double Pearson(long[] x, long[] y, int lag)
        {
            int n = x.Length;
            int start = Math.Max(0, -lag);
            int end = Math.Min(n, n - lag);
            int count = end - start;
            if (count < 3) return 0;

            double sx = 0, sy = 0;
            for (int k = start; k < end; k++) { sx += x[k]; sy += y[k + lag]; }
            double mx = sx / count, my = sy / count;

            double cov = 0, vx = 0, vy = 0;
            for (int k = start; k < end; k++)
            {
                double dx = x[k] - mx, dy = y[k + lag] - my;
                cov += dx * dy; vx += dx * dx; vy += dy * dy;
            }
            if (vx <= 0 || vy <= 0) return 0;
            return cov / Math.Sqrt(vx * vy);
        }

        /// <summary>
        /// Emits the chart section: an inlined uPlot (JS+CSS passed in), a data JSON blob, the analytics
        /// callouts (burst stats, onset markers via uPlot hooks, lead-lag lines), and the controls
        /// (bars/lines toggle, log-Y toggle, bin-size note). Returns "" when there's no data.
        /// </summary>
        public static string BuildChartHtml(TimelineModel m, string uplotJs, string uplotCss)
        {
            if (m == null || !m.HasData) return "";

            const int MaxLeadLagRows = 8;

            var sb = new StringBuilder();
            sb.Append("<style>").Append(uplotCss).Append("</style>");
            sb.Append("<div class=\"chart-card\">");

            // Pattern key: the ONE place the full templates live. Everything else references the short id.
            sb.Append("<table class=\"pat-key\"><thead><tr><th></th><th>ID</th><th>Pattern</th></tr></thead><tbody>");
            for (int i = 0; i < m.Ids.Count; i++)
            {
                sb.Append("<tr>")
                  .Append("<td><span class=\"swatch\" style=\"background:").Append(Esc(m.Colors[i])).Append("\"></span></td>")
                  .Append("<td class=\"pid\">").Append(Esc(m.Ids[i])).Append("</td>")
                  .Append("<td class=\"tmpl\" title=\"").Append(Esc(m.Templates[i])).Append("\">")
                  .Append(Esc(Middle(m.Templates[i], 120))).Append("</td>")
                  .Append("</tr>");
            }
            sb.Append("</tbody></table>");

            sb.Append("<div class=\"chart-controls\">");
            sb.Append("<label><input type=\"checkbox\" id=\"logY\"> Log Y</label>");
            sb.Append("<label><input type=\"checkbox\" id=\"asBars\"> Bars</label>");
            sb.Append("<span class=\"hint\">Drag to zoom · double-click to reset · click a legend series to toggle</span>");
            sb.Append("</div>");
            sb.Append("<div id=\"tlchart\"></div>");

            // Lead-lag callouts — reference ids only; capped so many patterns don't produce a wall.
            if (m.LeadLags.Count > 0)
            {
                sb.Append("<div class=\"leadlag\"><b>Possible relationships</b><ul>");
                int shown = Math.Min(MaxLeadLagRows, m.LeadLags.Count);
                for (int k = 0; k < shown; k++)
                {
                    var ll = m.LeadLags[k];
                    string rel;
                    if (ll.LagMs == 0) rel = $"{Esc(ll.A)} and {Esc(ll.B)} move together";
                    else if (ll.LagMs > 0) rel = $"{Esc(ll.A)} leads {Esc(ll.B)} by {FormatMs(ll.LagMs)}";
                    else rel = $"{Esc(ll.B)} leads {Esc(ll.A)} by {FormatMs(-ll.LagMs)}";
                    sb.Append($"<li>{rel} <span class=\"r\">(r={ll.R.ToString("0.00", CultureInfo.InvariantCulture)})</span></li>");
                }
                sb.Append("</ul>");
                if (m.LeadLags.Count > shown)
                    sb.Append($"<div class=\"hint\">…and {m.LeadLags.Count - shown} more relationship(s) below the top {shown}.</div>");
                sb.Append("<div class=\"hint\">Correlation of binned counts; a hint to investigate, not proof of causation.</div></div>");
            }

            // Burst stats — swatch + id + counts.
            sb.Append("<div class=\"bursts\"><b>Burst summary</b><ul>");
            foreach (var a in m.Analytics)
            {
                sb.Append("<li><span class=\"swatch\" style=\"background:")
                  .Append(Esc(a.Color)).Append("\"></span>")
                  .Append("<b>").Append(Esc(a.Id)).Append("</b>")
                  .Append($": {a.Count:N0} events; densest {FormatMs(m.BinMs)} window held {a.PeakBinCount:N0} ")
                  .Append($"({a.BurstSharePct.ToString("0", CultureInfo.InvariantCulture)}% of them)</li>");
            }
            sb.Append("</ul></div>");

            // Data JSON: aligned arrays uPlot expects — [ xEdges, series0, series1, ... ].
            sb.Append("<script>");
            sb.Append("var TL=").Append(BuildDataJson(m)).Append(";");
            sb.Append(ChartScript());
            sb.Append("</script>");
            // uPlot library last so it's defined before our init runs on DOMContentLoaded.
            sb.Append("<script>").Append(uplotJs).Append("</script>");
            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>Middle-truncates a long string to <paramref name="max"/> chars: head…tail (full text in a tooltip).</summary>
        internal static string Middle(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            int head = (max * 2) / 3;
            int tail = max - head - 1;
            if (tail < 1) tail = 1;
            return s.Substring(0, head) + "…" + s.Substring(s.Length - tail);
        }

        private static string BuildDataJson(TimelineModel m)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"binMs\":").Append(m.BinMs).Append(",");
            // x values in SECONDS since epoch (uPlot's time scale expects seconds).
            sb.Append("\"x\":[");
            for (int i = 0; i < m.BinCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((m.BinEdgesMs[i] / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
            }
            sb.Append("],");
            sb.Append("\"ids\":[").Append(string.Join(",", m.Ids.Select(JsonStr))).Append("],");
            sb.Append("\"colors\":[").Append(string.Join(",", m.Colors.Select(JsonStr))).Append("],");
            sb.Append("\"onsets\":[").Append(string.Join(",", m.Analytics.Select(a => (a.OnsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)))).Append("],");
            sb.Append("\"series\":[");
            for (int s = 0; s < m.Counts.Count; s++)
            {
                if (s > 0) sb.Append(',');
                sb.Append('[');
                var c = m.Counts[s];
                for (int i = 0; i < c.Length; i++) { if (i > 0) sb.Append(','); sb.Append(c[i]); }
                sb.Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // The client-side chart wiring. Kept as a plain string (no interpolation) so it's auditable.
        private static string ChartScript()
        {
            return @"
document.addEventListener('DOMContentLoaded', function () {
  var el = document.getElementById('tlchart');
  if (!el || typeof uPlot === 'undefined') return;
  var data = [TL.x].concat(TL.series);
  var logY = document.getElementById('logY');
  var asBars = document.getElementById('asBars');

  function mkSeries(bars) {
    var out = [{ label: 'Time' }];
    for (var i = 0; i < TL.ids.length; i++) {
      out.push({
        label: TL.ids[i], stroke: TL.colors[i],
        fill: bars ? TL.colors[i] : null,
        width: 2, points: { show: false },
        paths: bars ? uPlot.paths.bars({ size: [0.9, 100] }) : undefined,
        value: function (u, v) { return v == null ? '--' : v; }
      });
    }
    return out;
  }

  // Onset markers: vertical lines at each series' first event, drawn in the series color.
  function drawOnsets(u) {
    var ctx = u.ctx; ctx.save();
    ctx.lineWidth = 1; ctx.setLineDash([4, 3]);
    for (var i = 0; i < TL.onsets.length; i++) {
      var x = u.valToPos(TL.onsets[i], 'x', true);
      if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) continue;
      ctx.strokeStyle = TL.colors[i]; ctx.globalAlpha = 0.5;
      ctx.beginPath(); ctx.moveTo(x, u.bbox.top); ctx.lineTo(x, u.bbox.top + u.bbox.height); ctx.stroke();
    }
    ctx.restore();
  }

  var chart;
  function build() {
    if (chart) { chart.destroy(); chart = null; }
    var bars = asBars.checked;
    var opts = {
      width: Math.max(720, el.clientWidth || 900), height: 340,
      title: 'Event timeline (per pattern)',
      scales: { x: { time: true }, y: { distr: logY.checked ? 3 : 1 } },
      series: mkSeries(bars),
      cursor: { drag: { x: true, y: false } },
      hooks: { draw: [drawOnsets] }
    };
    chart = new uPlot(opts, data, el);
  }
  logY.addEventListener('change', build);
  asBars.addEventListener('change', build);
  window.addEventListener('resize', function () { if (chart) chart.setSize({ width: Math.max(720, el.clientWidth || 900), height: 340 }); });
  build();
});
";
        }

        internal static string FormatMs(long ms)
        {
            if (ms < 1000) return ms + " ms";
            double s = ms / 1000.0;
            if (s < 60) return Trim(s) + " s";
            double mnt = s / 60.0;
            if (mnt < 60) return Trim(mnt) + " min";
            double h = mnt / 60.0;
            if (h < 24) return Trim(h) + " h";
            return Trim(h / 24.0) + " d";
        }

        private static string Trim(double v) =>
            v.ToString(Math.Abs(v - Math.Round(v)) < 0.05 ? "0" : "0.0", CultureInfo.InvariantCulture);

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // Minimal JSON string encoder (control chars + quotes/backslashes) for labels/colors.
        private static string JsonStr(string s)
        {
            var sb = new StringBuilder(s == null ? 2 : s.Length + 2);
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break; // avoid closing a <script> in-string
                    case '>': sb.Append("\\u003e"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
