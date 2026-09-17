// http_timeout.js — flags socket/HTTP read timeouts (SocketTimeoutException).
// See google_oidc.js for the full description of the entry fields and the prefilter gate.
({
  name: "HTTP timeout",
  prefilter: ["SocketTimeoutException"],
  descriptions: {
    http_timeout: "A socket/HTTP read timed out (SocketTimeoutException) - a slow or unresponsive endpoint."
  },

  classify: function (entry) {
    if ((entry.content || "").indexOf("SocketTimeoutException") >= 0) {
      return "http_timeout";
    }
    return null;
  }
})
