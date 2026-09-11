using System;
using System.Collections.Generic;
using UnityEngine;

// Build/developer configuration for the Flight Plan publish transport. This is NOT player
// consent — see TelemetryConsent.PublishFlightPlansEnabled for the player-facing "Publish Flight
// Plans" preference. Sending requires both this config to be enabled AND the player to have
// opted in.
[CreateAssetMenu(fileName = "TelemetryConfig", menuName = "Astral Planes/Platform/Telemetry Config")]
public class TelemetryConfig : ScriptableObject
{
    // PhaseTransitionManager.CurrentPhaseId (-> MotifVisit.PhaseId) is Phase.phaseId, an
    // auto-assigned GUID string (Assets/Resources/Phase Library.asset) — not the ordinal "1/2/3"
    // the Flight Plan API expects. There is no way to derive one from the other, so the mapping
    // is authored explicitly here rather than guessed. Pre-populated from the real phaseId values
    // in "Phase Library.asset" as of this writing (Steam Demo=1, EP=2, Next=3, in authored list
    // order) — update this list if phases are added/reordered/regenerated.
    [Serializable]
    public class PhaseIdMappingEntry
    {
        public string phaseId;
        public int phaseNumber;
    }

    [Header("Endpoint")]
    [Tooltip("Must be https://. Bake in any deployment-specific path prefix (e.g. a trailing " +
             "/public if the host serves the package's public/ directory directly) — publish.php " +
             "and remove.php are appended to this as /api/publish.php and /api/remove.php.")]
    public string baseUrl;

    [Header("Behavior")]
    [Tooltip("Build/developer kill switch. Player consent (TelemetryConsent) is required in addition to this.")]
    public bool enabled = true;
    [Tooltip("If false (default), uploads never fire while running in the Unity Editor, even if enabled is true.")]
    public bool sendInEditor = false;
    public float requestTimeoutSeconds = 10f;
    public int maxRetryAttemptsPerLaunch = 3;

    [Header("Testing")]
    [Tooltip("TESTING ONLY: publishes automatically without requiring TelemetryConsent.PublishFlightPlansEnabled. " +
             "Every send this bypasses is logged unconditionally. Must be false before shipping.")]
    public bool bypassConsentForTesting = false;
    [Tooltip("TESTING ONLY: allows a plain http:// baseUrl while the server doesn't have TLS yet. " +
             "Every send this bypasses is logged unconditionally. Must be false before shipping — " +
             "Flight Plan data (including publisher_token) would otherwise cross the network in the clear.")]
    public bool allowInsecureHttpForTesting = false;

    [Header("Phase ID Mapping")]
    [Tooltip("Explicit Phase.phaseId (GUID) -> Flight Plan phase-number mapping. A run whose " +
             "phaseId isn't listed here is refused rather than guessed at.")]
    public List<PhaseIdMappingEntry> phaseIdMapping = new List<PhaseIdMappingEntry>
    {
        new PhaseIdMappingEntry { phaseId = "2cfde5cf3d2048b8910ab984b7267338", phaseNumber = 1 }, // Steam Demo
        new PhaseIdMappingEntry { phaseId = "e80b2c210dcf4b40ba9f3f3542b20d69", phaseNumber = 2 }, // EP
        new PhaseIdMappingEntry { phaseId = "7372954664d149969c3d8cf05cef0823", phaseNumber = 3 }, // Next
    };

    public string PublishUrl => string.IsNullOrEmpty(baseUrl) ? null : baseUrl.TrimEnd('/') + "/api/publish.php";
    public string RemoveUrl => string.IsNullOrEmpty(baseUrl) ? null : baseUrl.TrimEnd('/') + "/api/remove.php";

    public bool TryGetPhaseNumber(string phaseId, out int phaseNumber)
    {
        if (!string.IsNullOrEmpty(phaseId) && phaseIdMapping != null)
        {
            foreach (var entry in phaseIdMapping)
            {
                if (entry != null && entry.phaseId == phaseId)
                {
                    phaseNumber = entry.phaseNumber;
                    return true;
                }
            }
        }

        phaseNumber = 0;
        return false;
    }
}
