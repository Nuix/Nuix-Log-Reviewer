// Example scripted insight detector for the Nuix Log Reviewer.
//
// A script evaluates to an object with an analyze(ctx) function that returns an array of findings.
// It runs ONCE over a read-only snapshot of the whole loaded corpus (not per line), so you can loop
// over the summaries freely. Everything is sandboxed: no file, network, or host access.
//
// ctx shape (all read-only):
//   ctx.total, ctx.info, ctx.warn, ctx.error, ctx.debug     - whole-set line counts
//   ctx.minTicks, ctx.maxTicks                               - .NET DateTime ticks of the time span
//   ctx.flags    : [{ name, count, query }]                  - classifier hits
//   ctx.files    : [{ path, displayName, count, query }]     - per source file
//   ctx.jobs     : [{ jobId, entryCount, warn, error, durationMs, firstTicks, lastTicks, query }]
//   ctx.patterns : [{ template, count, info, warn, error, debug, firstTicks, lastTicks, query }]
//
// Each item's `query` is a ready-to-run query string (correctly escaped; patterns are member-expanded).
// Copy it into a finding's `query` so double-clicking the finding jumps straight to the evidence.
//
// Finding shape (title is required; everything else optional):
//   { severity: "info" | "notice" | "warning",
//     title: "short headline",
//     detail: "one-line supporting detail",
//     query: "<a query string, e.g. from an item's .query>",
//     positionTicks: <DateTime ticks to scroll to>,   // optional
//     positionEntryId: <entry id to select> }          // optional

var name = "Example (scripted)";

function analyze(ctx) {
    var out = [];

    // 1) Any job whose error rate is very high (a likely failed job).
    for (var i = 0; i < ctx.jobs.length; i++) {
        var j = ctx.jobs[i];
        if (j.entryCount > 0 && j.error / j.entryCount >= 0.5 && j.error >= 25) {
            out.push({
                severity: "warning",
                title: "Job " + j.jobId + " failed heavily (script)",
                detail: j.error + " errors of " + j.entryCount + " entries",
                query: j.query
            });
        }
    }

    // 2) The most frequent WARNING-leaning pattern (noise worth reviewing or hiding).
    var topWarn = null;
    for (var p = 0; p < ctx.patterns.length; p++) {
        var pat = ctx.patterns[p];
        if (pat.warn > pat.error && pat.warn > pat.info) {
            if (topWarn === null || pat.count > topWarn.count) { topWarn = pat; }
        }
    }
    if (topWarn !== null && topWarn.count >= 100) {
        out.push({
            severity: "notice",
            title: "Frequent warning pattern (script)",
            detail: topWarn.count + "x: " + topWarn.template,
            query: topWarn.query
        });
    }

    return out;
}

// Expose the object (the last expression is the module value).
({ name: name, analyze: analyze });
