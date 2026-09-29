// Worker-watchdog timeout insight detectors (scripted, customizable).
//
// The Nuix worker watchdog logs an ERROR when a worker makes NO progress on an item for longer than a
// configured timeout, e.g.:
//   "[Worker Watchdog] ... ERROR ... Worker has not progressed within timeout of: 600 seconds,
//    current item: (<id>: application/vnd.nuix-evidence/...)"
// Each such line means the worker stalled for AT LEAST that many seconds. Summed, they estimate how
// much of a run was lost to stalls - which can be enormous (e.g. 3.75 h of stalls inside a 4 h run).
//
// This surfaces two things:
//   1) The overall watchdog-timeout load across the whole run (count + summed stall time).
//   2) Individual worker JOBS that stalled repeatedly (count + summed stall time, and - when the job's
//      runtime is known - what fraction of the job's wall-clock was spent stalled).
//
// It runs over the WHOLE loaded corpus via analyze(ctx); being a script, it's re-read on every
// "Analyze" - tweak the thresholds below and re-run, no restart. See example_insight.js for the full
// ctx/finding shape.
//
// The data this script uses:
//   ctx.workerTimeouts : { total, totalSeconds, byJob: [{ jobId, count, seconds, workerCount, query }] }
//        Present only when the corpus has watchdog-timeout lines. `seconds` is the SUMMED timeout
//        seconds ACROSS ALL of the job's workers (a lower-bound estimate of stall time); `workerCount`
//        is how many distinct workers in that job timed out. Each per-job `query` isolates that job's
//        timeout lines. Absent (undefined) when there were none.
//   ctx.jobs : [{ jobId, entryCount, warn, error, durationMs, firstTicks, lastTicks, query }]
//        Used to state the job's wall-clock runtime alongside its total stall time.

var name = "Worker timeouts";

// ---- Tunables ---------------------------------------------------------------------------------------
var MIN_TOTAL_TIMEOUTS = 1;   // surface the overall finding once this many watchdog timeouts occur
var MIN_JOB_TIMEOUTS   = 3;   // flag an individual job once it has at least this many timeouts
var MAX_JOB_FINDINGS   = 10;  // cap per-job findings so a very broken run doesn't flood the panel

// Compact "3.7 h", "45 m" or "30 s" from seconds.
function hms(sec) {
    if (sec >= 3600) { return (sec / 3600).toFixed(2) + " h"; }
    if (sec >= 60)   { return (sec / 60).toFixed(1) + " m"; }
    return Math.round(sec) + " s";
}

function analyze(ctx) {
    var out = [];
    var wt = ctx.workerTimeouts;
    if (!wt || wt.total < MIN_TOTAL_TIMEOUTS) { return out; }

    // 1) Overall watchdog-timeout load. Severity scales a bit with how much time was lost.
    var totalStall = hms(wt.totalSeconds);
    out.push({
        severity: wt.totalSeconds >= 3600 ? "warning" : "notice",
        title: "Worker watchdog timeouts: " + wt.total + " (~" + totalStall + " stalled)",
        detail: wt.total + " worker-watchdog timeout(s) fired across the run. Each means a worker made "
              + "no progress on an item for the whole timeout, so at least ~" + totalStall + " was lost "
              + "to stalls (a lower-bound estimate). Repeated timeouts usually point at a small number "
              + "of items or a resource problem (I/O, memory, a hung dependency).",
        query: "content:\"has not progressed within timeout\""
    });

    // Build a jobId -> runtime(ms) lookup so per-job findings can show the stall/runtime fraction.
    var runtimeByJob = {};
    if (ctx.jobs) {
        for (var i = 0; i < ctx.jobs.length; i++) {
            runtimeByJob[ctx.jobs[i].jobId] = ctx.jobs[i].durationMs;
        }
    }

    // 2) Timeout-heavy jobs (byJob is pre-sorted by seconds desc). Cap the number surfaced.
    if (wt.byJob) {
        var emitted = 0;
        for (var k = 0; k < wt.byJob.length && emitted < MAX_JOB_FINDINGS; k++) {
            var j = wt.byJob[k];
            if (j.count < MIN_JOB_TIMEOUTS) { continue; }
            emitted++;

            var stall = hms(j.seconds);

            // State the raw numbers rather than a percentage: a "% of runtime" needs a denominator that
            // misleads here (workers run in PARALLEL, so summed stall can exceed wall-clock elapsed).
            // Instead we say "~3.67 h of stall across N workers" against "the job ran 1.26 h wall-clock"
            // - the N-workers context makes the (legitimately) larger stall total read as informative,
            // not a broken value, and lets the reader judge the magnitude directly.
            var acrossWorkers = j.workerCount > 1 ? (" across " + j.workerCount + " workers") : "";

            var runtimeMs = runtimeByJob[j.jobId];
            var vsRuntime = "";
            var stallBound = "";
            if (runtimeMs && runtimeMs > 0) {
                var runtimeSec = runtimeMs / 1000;
                vsRuntime = " The job ran " + hms(runtimeSec) + " wall-clock.";
                // Soft qualitative flag when total stall is on the order of (or beyond) the whole run.
                if (j.seconds >= runtimeSec) {
                    stallBound = " Total stall time exceeded the job's wall-clock runtime, so this run "
                               + "was heavily stall-bound.";
                }
            }

            var label = j.jobId || "(unknown job)";
            out.push({
                severity: "warning",
                title: "Job stalled repeatedly: " + label,
                detail: j.count + " worker-watchdog timeout(s) in this job total ~" + stall
                      + " of stalled processing" + acrossWorkers + "." + vsRuntime + stallBound
                      + " Opens this job's timeout lines. This is often a handful of problem items or a "
                      + "resource bottleneck - worth isolating.",
                query: j.query
            });
        }
    }

    return out;
}

// Expose the module object (last expression is the module value).
({ name: name, analyze: analyze });
