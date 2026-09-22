// adaptive_security.js — scripted classifiers for Nuix Adaptive Security service logs.
//
// Adaptive Security runs as a set of .NET microservices (adaptive-api, event-enricher,
// event-shuttle, retry-enricher, eps, ...) whose logs use the bracketed format
//   <k8s-ts> [<ts> LEVEL] <Source>() | <message>
// This classifier tags the recurring operational failure modes seen in those services so you can
// filter/facet them in the grid (flag:<name>) and see them in the Classifiers tab. Each flag is an
// INDEPENDENT signal; a single line normally matches at most one.
//
// See google_oidc.js for the full description of the read-only `entry` fields, the `prefilter`
// host-side gate, `descriptions`, and flag normalization. Nothing here matches on customer data
// (endpoint / process ids etc.) — only on stable component names and framework/message phrases.
({
  name: "Adaptive Security",

  // Fast host-side gate: classify() only runs for lines containing one of these (case-insensitive),
  // so the vast majority of lines never enter the engine.
  prefilter: [
    "invalid state transition",
    "couldn't enrich event",
    "process summary",
    "invalid elasticsearch response",
    "unknown topic or partition",
    "maximum poll interval"
  ],

  descriptions: {
    adaptive_state_transition:
      "Adaptive task assignment hit an invalid state transition (e.g. FINISHED -> SENT); the assignment couldn't be updated.",
    adaptive_enrich_failure:
      "Adaptive event enrichment failed for an event (missing Process Summary for the endpoint/process); the event was delayed to a retry topic.",
    adaptive_es_bulk_failure:
      "An Elasticsearch bulk insert failed (invalid Elasticsearch response); enriched events may not have landed in the index.",
    adaptive_kafka_topic:
      "A Kafka consumer subscribed to a topic that wasn't available (unknown topic or partition) — likely a missing topic or a config/deployment mismatch.",
    adaptive_kafka_poll:
      "A Kafka consumer exceeded its maximum poll interval and left the group — processing back-pressure / a slow consumer."
  },

  classify: function (entry) {
    var c = entry.content || "";

    // Task lifecycle: "... Invalid state transition FINISHED -> SENT"
    if (c.indexOf("Invalid state transition") >= 0) {
      return "adaptive_state_transition";
    }

    // Enrichment: "Couldn't enrich event ..." (WARN) and the paired
    // "Process Summary for endpoint ... not found" (ERROR) both signal an enrichment miss.
    if (c.indexOf("Couldn't enrich event") >= 0 ||
        c.indexOf("Process Summary") >= 0) {
      return "adaptive_enrich_failure";
    }

    // Elasticsearch bulk insert: "Bulk operation failed: Invalid Elasticsearch response ..."
    if (c.indexOf("Invalid Elasticsearch response") >= 0) {
      return "adaptive_es_bulk_failure";
    }

    // Kafka: unavailable topic vs. poll-interval eviction are distinct root causes.
    if (c.indexOf("Unknown topic or partition") >= 0) {
      return "adaptive_kafka_topic";
    }
    if (c.indexOf("maximum poll interval") >= 0) {
      return "adaptive_kafka_poll";
    }

    return null;
  }
})
