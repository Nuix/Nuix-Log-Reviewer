// Workflow-execution insight detectors (scripted, customizable).
//
// These surface things worth pulling on about Automate WORKFLOW EXECUTIONS mined from the engine logs:
//   1) A workflow that STARTED but never completed (crashed / killed / truncated log) - usually the
//      single most important reason someone opened the log.
//   2) An OPERATION during which a meaningful number of errors/warnings were logged, i.e.
//      "this operation is where the errors happened" - links the workflow track to log severity.
//
// It runs over the WHOLE loaded corpus via analyze(ctx). Because it's a script, it's re-read on every
// "Analyze" - edit the thresholds below and re-run, no restart. See example_insight.js for the full
// ctx/finding shape.
//
// The workflow data this script uses:
//   ctx.workflows : [{ name, startTicks, endTicks, durationMs, incomplete, operationCount, sourceFile,
//                      query,
//                      operations: [{ index, total, name, startTicks, endTicks, durationMs,
//                                     incomplete, waitedOnJobId, warns, errors, query }] }]
//       Each operation's `warns`/`errors` are the EXACT count of WARN/ERROR lines whose timestamp falls
//       in that operation's [start,end] window across the whole corpus (so errors logged by a worker job
//       running during the operation are attributed to it) - not a chart-bucket approximation.
// Each workflow/operation carries a ready-to-run `query` (the workflow's timestamp range), so a finding
// just copies it and optionally sets positionTicks to land on the operation's own start.

var name = "Workflow executions";

// ---- Tunables ---------------------------------------------------------------------------------------
var MIN_OP_ERRORS = 10;   // errors within an operation's window before we flag it
var MIN_OP_WARNS  = 50;   // OR this many warnings (an op can be warn-heavy without hard errors)
var MAX_OP_FINDINGS = 8;  // cap operation findings so a bad run doesn't flood the panel

// Compact "1h 2m", "3m 4s" or "500ms" from milliseconds.
function dur(ms) {
    if (ms < 1000) { return Math.round(ms) + "ms"; }
    var s = Math.floor(ms / 1000);
    var h = Math.floor(s / 3600); s -= h * 3600;
    var m = Math.floor(s / 60);   s -= m * 60;
    if (h > 0) { return h + "h " + m + "m"; }
    if (m > 0) { return m + "m " + s + "s"; }
    return s + "s";
}

function analyze(ctx) {
    var out = [];
    if (!ctx.workflows || ctx.workflows.length === 0) { return out; }

    // 1) Incomplete workflows: started, never observed to finish. Critical - the run didn't complete.
    for (var i = 0; i < ctx.workflows.length; i++) {
        var w = ctx.workflows[i];
        if (!w.incomplete) { continue; }
        out.push({
            severity: "critical",
            title: "Workflow did not complete: " + (w.name || "(unnamed)"),
            detail: "This workflow started but no completion was logged (crash, kill, or truncated log). "
                  + "Ran for at least " + dur(w.durationMs) + " across " + w.operationCount
                  + " operation(s). Opens its full time range.",
            query: w.query,
            positionTicks: w.startTicks
        });
    }

    // 2) Error/warning-heavy operations: an operation during whose window many errors/warnings were
    //    logged. Counts are exact and per-operation (op.errors / op.warns), so two sub-second operations
    //    no longer share the same chart-bucket total. Collect candidates first to rank and cap them.
    var opFindings = [];
    for (var wi = 0; wi < ctx.workflows.length; wi++) {
        var wf = ctx.workflows[wi];
        if (!wf.operations) { continue; }
        for (var oi = 0; oi < wf.operations.length; oi++) {
            var op = wf.operations[oi];
            if (op.endTicks <= op.startTicks) { continue; } // no measurable window (e.g. incomplete op)
            if (op.errors < MIN_OP_ERRORS && op.warns < MIN_OP_WARNS) { continue; }

            var opLabel = op.name || ("operation " + op.index + "/" + op.total);
            opFindings.push({
                score: op.errors * 1000000 + op.warns, // errors dominate the ranking
                finding: {
                    severity: op.errors >= MIN_OP_ERRORS ? "warning" : "notice",
                    title: "Errors during operation: " + opLabel,
                    detail: op.errors + " error(s) and " + op.warns + " warning(s) were logged while "
                          + "\"" + opLabel + "\" ran (" + dur(op.durationMs) + ") in workflow \""
                          + (wf.name || "(unnamed)") + "\". Opens the operation's window.",
                    query: op.query,
                    positionTicks: op.startTicks
                }
            });
        }
    }
    opFindings.sort(function (a, b) { return b.score - a.score; });
    for (var k = 0; k < opFindings.length && k < MAX_OP_FINDINGS; k++) {
        out.push(opFindings[k].finding);
    }

    return out;
}

// Expose the module object (last expression is the module value).
({ name: name, analyze: analyze });
