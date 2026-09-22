// Adaptive Security insight detectors (scripted, customizable).
//
// These surface the recurring, operationally-meaningful signals seen in Adaptive Security
// microservice logs (adaptive-api / eps / event-enricher / event-shuttle / retry-enricher):
//   - event enrichment failures (the dominant health signal for the enrichers)
//   - Elasticsearch bulk-insert failures (the event-shuttle sink)
//   - Kafka consume problems: consumer stalls (max poll interval) and missing topics
//   - task-assignment "Invalid state transition" errors (adaptive-api)
//   - Kafka client config warnings (low-severity noise)
//
// It runs over the WHOLE loaded corpus via analyze(ctx). Because it's a script, it's re-read on every
// "Analyze" - edit the thresholds/phrases below and re-run, no restart. See example_insight.js for the
// full ctx/finding shape. Each ctx.patterns item has: { template, count, info, warn, error, debug,
// firstTicks, lastTicks, query } and query is ready to run (drills to that pattern).
//
// HOW TO CUSTOMIZE: each detector below is just "scan ctx.patterns for a phrase, sum the counts, and
// emit a finding if the total crosses a threshold". Copy a block, change the phrase and threshold.

var name = "Adaptive Security";

// ---- Tunables ---------------------------------------------------------------------------------------
var MIN_ENRICH_FAILURES   = 50;   // event enrichment failures before we flag
var MIN_ES_BULK_FAILURES  = 5;    // Elasticsearch bulk failures
var MIN_KAFKA_POLL        = 1;    // "maximum poll interval exceeded" (a stall is notable even once)
var MIN_UNKNOWN_TOPIC     = 1;    // "Unknown topic or partition" (config/deploy problem)
var MIN_STATE_TRANSITION  = 25;   // task-assignment invalid state transitions
var MIN_CONFWARN          = 1;    // kafka client config warnings

// Case-insensitive "template contains phrase".
function has(t, phrase) {
    return t && t.toLowerCase().indexOf(phrase.toLowerCase()) >= 0;
}

// Aggregate all patterns whose template matches predicate(t): returns { count, top } where top is the
// highest-count matching pattern (used for the finding's jump query) or null if none matched.
function aggregate(ctx, predicate) {
    var total = 0, top = null;
    for (var i = 0; i < ctx.patterns.length; i++) {
        var p = ctx.patterns[i];
        if (predicate(p.template)) {
            total += p.count;
            if (top === null || p.count > top.count) { top = p; }
        }
    }
    return { count: total, top: top };
}

function analyze(ctx) {
    var out = [];

    // 1) Event enrichment failures (enrichers). Two phrasings appear: "Couldn't enrich event" and
    //    "Failed to enrich event"; the underlying cause is usually "Process Summary ... not found".
    var enrich = aggregate(ctx, function (t) {
        return has(t, "enrich event") || has(t, "Process Summary for endpoint");
    });
    if (enrich.count >= MIN_ENRICH_FAILURES && enrich.top) {
        out.push({
            severity: "warning",
            title: "Event enrichment failing (" + enrich.count + ")",
            detail: enrich.count + " enrichment failures - events couldn't be enriched (often a missing "
                  + "process summary for the endpoint). Check the enricher / upstream process data.",
            query: enrich.top.query
        });
    }

    // 2) Elasticsearch bulk-insert failures (event-shuttle sink).
    var es = aggregate(ctx, function (t) { return has(t, "Invalid Elasticsearch response")
                                               || has(t, "Bulk operation failed"); });
    if (es.count >= MIN_ES_BULK_FAILURES && es.top) {
        out.push({
            severity: "warning",
            title: "Elasticsearch bulk inserts failing (" + es.count + ")",
            detail: es.count + " bulk operations failed (invalid Elasticsearch response) - events may not "
                  + "be landing in the index. Check Elasticsearch health / mappings.",
            query: es.top.query
        });
    }

    // 3) Kafka consumer stall: "Application maximum poll interval exceeded" (the consumer is too slow
    //    and got kicked from the group - a throughput / back-pressure problem).
    var poll = aggregate(ctx, function (t) { return has(t, "maximum poll interval"); });
    if (poll.count >= MIN_KAFKA_POLL && poll.top) {
        out.push({
            severity: "warning",
            title: "Kafka consumer stalled (max poll interval exceeded)",
            detail: poll.count + "x: the consumer exceeded its max poll interval and left the group - a "
                  + "processing back-pressure / slow-consumer signal.",
            query: poll.top.query
        });
    }

    // 4) Kafka missing topics: "Unknown topic or partition" (usually a config/deployment gap).
    var topic = aggregate(ctx, function (t) { return has(t, "Unknown topic or partition")
                                                  || has(t, "Subscribed topic not available"); });
    if (topic.count >= MIN_UNKNOWN_TOPIC && topic.top) {
        out.push({
            severity: "warning",
            title: "Kafka topic(s) unavailable",
            detail: topic.count + "x: a subscribed topic was not available (unknown topic/partition) - "
                  + "likely a missing topic or a config/deployment mismatch.",
            query: topic.top.query
        });
    }

    // 5) Task-assignment invalid state transitions (adaptive-api).
    var state = aggregate(ctx, function (t) { return has(t, "Invalid state transition"); });
    if (state.count >= MIN_STATE_TRANSITION && state.top) {
        out.push({
            severity: "warning",
            title: "Task-assignment state errors (" + state.count + ")",
            detail: state.count + " invalid task state transitions (e.g. FINISHED -> SENT) - task "
                  + "assignments couldn't be updated. Check the task lifecycle / ordering.",
            query: state.top.query
        });
    }

    // 6) Kafka client config warnings (CONFWARN) - low-severity noise, surfaced as a Notice.
    var conf = aggregate(ctx, function (t) { return has(t, "CONFWARN") || has(t, "is a consumer property"); });
    if (conf.count >= MIN_CONFWARN && conf.top) {
        out.push({
            severity: "notice",
            title: "Kafka client config warnings (" + conf.count + ")",
            detail: conf.count + " librdkafka configuration warnings (a property ignored by this "
                  + "producer/consumer instance) - usually benign, but worth a glance.",
            query: conf.top.query
        });
    }

    return out;
}

// Expose the module object (last expression is the module value).
({ name: name, analyze: analyze });
