// third_party_rest.js — flags third-party REST integration failures (ThirdPartyRestException),
// which can appear in either the message content or the source. See google_oidc.js for details.
({
  name: "Third-party REST",
  prefilter: ["ThirdPartyRestException"],
  descriptions: {
    third_party_rest: "A third-party REST integration failed (ThirdPartyRestException), seen in the message or source."
  },

  classify: function (entry) {
    var needle = "ThirdPartyRestException";
    if ((entry.content || "").indexOf(needle) >= 0 ||
        (entry.source || "").indexOf(needle) >= 0) {
      return "third_party_rest";
    }
    return null;
  }
})
