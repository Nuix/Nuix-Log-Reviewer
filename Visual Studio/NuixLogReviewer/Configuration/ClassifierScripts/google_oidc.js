// google_oidc.js — example scripted classifier for the Nuix Log Reviewer.
//
// A classifier script evaluates to an object with a `classify(entry)` function. The function
// receives a READ-ONLY view of one log entry and returns a flag string, an array of flag strings,
// or null for "no flags". Returning an array lets ONE record carry MULTIPLE flags, e.g.
//   return ["http_error", "http_timeout"];
// Flags are lower_snake_cased automatically (and de-duplicated), then become searchable as
// flag:<name> and appear in the Classifiers tab.
//
// The engine is sandboxed: no file, network, or .NET access — only the `entry` fields below:
//   entry.content    (string)  the message body
//   entry.source     (string)  the logging source/class
//   entry.level      (string)  INFO | WARN | ERROR | DEBUG | ...
//   entry.channel    (string)
//   entry.filePath   (string)  full path of the source log file
//   entry.fileName   (string)
//   entry.timeStamp  (string)  ISO-8601
//   entry.elapsedMs  (number)
//   entry.lineNumber (number)
//
// `prefilter` (optional) is a fast host-side gate: classify() only runs for entries whose content
// OR source contains one of these literals (case-insensitive). Use it to keep loads fast.
//
// `descriptions` (optional) maps each flag this script can emit to a short description, shown in the
// Classifiers tab "Description" column so users understand the classifier's intent.
({
  name: "Google OIDC",
  prefilter: ["oidc.google", "GoogleOidcUserServiceClient", "GoogleUserServiceWorker"],
  descriptions: {
    google_oidc: "Entries from the Google OIDC / Google user-service code paths (identity / Vault integration activity)."
  },

  classify: function (entry) {
    var source = entry.source || "";
    if (source.indexOf("oidc.google") >= 0 ||
        source.indexOf("GoogleOidcUserServiceClient") >= 0 ||
        source.indexOf("GoogleUserServiceWorker") >= 0) {
      return "google_oidc";
    }
    return null;
  }
})
