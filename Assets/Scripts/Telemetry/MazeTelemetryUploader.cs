using System;
using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Publishes finished Greater Maze runs to the Flight Plan API as
// "add your journeys and musical solutions anonymously to the public Astral Planes Greater Maze."
// Never replaces the local disk write in MazeTelemetryStore — this is purely an additional sink,
// and publication failure must never affect gameplay (every path here is fail-silent, matching
// SteamStatsManager's discipline).
//
// Sending requires ALL of: TelemetryConfig.enabled (build/developer kill switch),
// TelemetryConsent.PublishFlightPlansEnabled (the player's actual "Publish Flight Plans" opt-in —
// playing the game, or any other analytics/consent setting, is never treated as consent for
// this), a valid HTTPS endpoint, and (outside the Editor, or with sendInEditor set) a resolvable
// publisher identity.
public class MazeTelemetryUploader : MonoBehaviour
{
    [SerializeField] private TelemetryConfig config;

    private void Start()
    {
        // Gated by ShouldAttemptSend (which includes the consent check), so this is a no-op — not
        // just "no upload happens" but no request is even attempted — until the player has
        // explicitly opted in at least once. Once opted in, treats the preference as ongoing
        // ("publish this and future locally saved flights") rather than a one-shot action, so a
        // later launch does flush anything that failed to send earlier.
        StartCoroutine(FlushPendingOnLaunch());
    }

    // Called from MazeTelemetryRecorder.FinalizeRun with the file MazeTelemetryStore just wrote.
    public void UploadRun(MazeRunRecord record, string sourceFilePath)
    {
        if (record == null || string.IsNullOrEmpty(sourceFilePath)) return;
        StartCoroutine(SendOne(record, sourceFilePath));
    }

    // Publishes one specific previously-saved run on demand (e.g. a future "publish this flight"
    // UI action), independent of the pending-queue flush.
    public void PublishSavedRunFile(string filePath)
    {
        var record = LoadRunFromFile(filePath);
        if (record != null) StartCoroutine(SendOne(record, filePath));
    }

    // Re-runs the same pending-queue flush as Start(), on demand — e.g. right after the player
    // grants consent for the first time, so a run saved earlier this session (when consent was
    // still false) gets sent immediately instead of waiting for the next app launch.
    public void FlushPendingNow() => StartCoroutine(FlushPendingOnLaunch());

    // "Remove My Published Flight Plans" — withdraws this anonymous publisher's contributions
    // from the public Greater Maze. Never touches GreaterMazeTelemetry/ or GreaterMazeTelemetry/Sent/:
    // this is a server-side withdrawal, not a local-history deletion.
    public void RemovePublishedFlightPlans(Action<bool> onComplete = null)
    {
        StartCoroutine(SendRemove(onComplete));
    }

    private IEnumerator FlushPendingOnLaunch()
    {
        if (!ShouldAttemptSend()) yield break;

        int maxAttempts = Mathf.Max(0, config.maxRetryAttemptsPerLaunch);
        var pending = TelemetryUploadQueue.PendingFiles().Take(maxAttempts).ToList();

        foreach (var path in pending)
        {
            var record = LoadRunFromFile(path);
            if (record != null) yield return SendOne(record, path);
        }
    }

    private IEnumerator SendOne(MazeRunRecord record, string sourceFilePath)
    {
        if (!ShouldAttemptSend()) yield break;

        string json;
        string failureReason;
        try
        {
            if (!FlightPlanPayloadBuilder.TryBuildPublishJson(
                    record, config, PublisherIdentity.PublisherId, PublisherIdentity.PublisherToken,
                    out json, out failureReason))
            {
                if (GameFlowManager.VerboseLogging)
                    Debug.LogWarning($"[MazeTelemetryUploader] Refusing to publish run {record.RunId}: {failureReason}");
                yield break;
            }
        }
        catch (Exception e)
        {
            if (GameFlowManager.VerboseLogging)
                Debug.LogWarning($"[MazeTelemetryUploader] Failed to build payload for {sourceFilePath}: {e.Message}");
            yield break;
        }

        UnityWebRequest req;
        try
        {
            req = BuildRequest(config.PublishUrl, json);
        }
        catch (Exception e)
        {
            if (GameFlowManager.VerboseLogging)
                Debug.LogWarning($"[MazeTelemetryUploader] Failed to build request for {sourceFilePath}: {e.Message}");
            yield break;
        }

        using (req)
        {
            yield return req.SendWebRequest();

            // Only HTTP 200 + a response body that parses with ok == true marks the run sent.
            // Transport-level "success" (no connection error) is not sufficient — the server can
            // return a well-formed error response on a perfectly successful HTTP round trip.
            if (req.responseCode == 200)
            {
                bool ok = false;
                try
                {
                    var response = JsonUtility.FromJson<FlightPlanPublishResponse>(req.downloadHandler.text);
                    ok = response != null && response.ok;
                }
                catch (Exception e)
                {
                    if (GameFlowManager.VerboseLogging)
                        Debug.LogWarning($"[MazeTelemetryUploader] Failed to parse publish response for run {record.RunId}: {e.Message}");
                }

                if (ok)
                {
                    TelemetryUploadQueue.MarkSent(sourceFilePath);
                    if (GameFlowManager.VerboseLogging)
                        Debug.Log($"[MazeTelemetryUploader] Published flight {record.RunId}");
                }
                else if (GameFlowManager.VerboseLogging)
                {
                    Debug.LogWarning($"[MazeTelemetryUploader] Publish response for run {record.RunId} was not ok — leaving pending.");
                }
            }
            else
            {
                LogPublishFailure(req.responseCode, req.downloadHandler?.text);
            }
        }
    }

    private IEnumerator SendRemove(Action<bool> onComplete)
    {
        bool ok = false;

        if (CanUseEndpoint(config?.RemoveUrl) && PublisherIdentity.TryGetExisting(out string publisherId, out string publisherToken))
        {
            string json = FlightPlanPayloadBuilder.BuildRemoveJson(publisherId, publisherToken);

            UnityWebRequest req = null;
            try
            {
                req = BuildRequest(config.RemoveUrl, json);
            }
            catch (Exception e)
            {
                if (GameFlowManager.VerboseLogging)
                    Debug.LogWarning($"[MazeTelemetryUploader] Failed to build remove request: {e.Message}");
            }

            if (req != null)
            {
                using (req)
                {
                    yield return req.SendWebRequest();

                    if (req.responseCode == 200)
                    {
                        try
                        {
                            var response = JsonUtility.FromJson<FlightPlanRemoveResponse>(req.downloadHandler.text);
                            ok = response != null && response.ok;
                        }
                        catch (Exception e)
                        {
                            if (GameFlowManager.VerboseLogging)
                                Debug.LogWarning($"[MazeTelemetryUploader] Failed to parse remove response: {e.Message}");
                        }
                    }
                    else
                    {
                        LogPublishFailure(req.responseCode, req.downloadHandler?.text);
                    }
                }
            }
        }
        else if (GameFlowManager.VerboseLogging)
        {
            Debug.LogWarning("[MazeTelemetryUploader] Cannot remove published Flight Plans: no publisher identity or endpoint configured.");
        }

        onComplete?.Invoke(ok);
    }

    // Publish-specific gating: config + endpoint + editor safety, AND the player's explicit
    // opt-in. TelemetryConfig.enabled is a build/developer switch, not consent — it can never
    // substitute for TelemetryConsent.PublishFlightPlansEnabled, except for the explicit,
    // loudly-logged testing bypass below.
    private bool ShouldAttemptSend()
    {
        if (!CanUseEndpoint(config?.PublishUrl)) return false;
        if (TelemetryConsent.PublishFlightPlansEnabled) return true;

        if (config.bypassConsentForTesting)
        {
            // Unconditional, not VerboseLogging-gated: this bypasses real player consent, so it
            // must never fire quietly.
            Debug.LogWarning("[MazeTelemetryUploader] Publishing without player consent — TelemetryConfig.bypassConsentForTesting is on. Turn this off before shipping.");
            return true;
        }

        return false;
    }

    // Shared by publish and remove: config/URL/HTTPS/editor gating, without the consent check —
    // removal must remain available even if the player has since turned publishing off, since a
    // prior contribution may already exist on the server.
    private bool CanUseEndpoint(string url)
    {
        if (config == null || !config.enabled) return false;
        if (string.IsNullOrEmpty(url)) return false;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            if (!isHttp || !config.allowInsecureHttpForTesting)
            {
                if (GameFlowManager.VerboseLogging)
                    Debug.LogWarning("[MazeTelemetryUploader] Refusing to send: endpoint must be https://");
                return false;
            }

            // Unconditional, not VerboseLogging-gated: this sends Flight Plan data (including
            // publisher_token) over plain HTTP, so it must never happen quietly.
            Debug.LogWarning("[MazeTelemetryUploader] Sending over plain HTTP — TelemetryConfig.allowInsecureHttpForTesting is on. Turn this off once the server has HTTPS.");
        }
        if (Application.isEditor && !config.sendInEditor) return false;
        return true;
    }

    private static MazeRunRecord LoadRunFromFile(string path)
    {
        try
        {
            return JsonUtility.FromJson<MazeRunRecord>(System.IO.File.ReadAllText(path));
        }
        catch (Exception e)
        {
            if (GameFlowManager.VerboseLogging)
                Debug.LogWarning($"[MazeTelemetryUploader] Failed to read run file {path}: {e.Message}");
            return null;
        }
    }

    private UnityWebRequest BuildRequest(string url, string json)
    {
        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        byte[] body = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = Mathf.Max(1, Mathf.RoundToInt(config.requestTimeoutSeconds));
        return req;
    }

    // Never logs publisher_token (or the raw response body — only the server's own `error` code,
    // when present). HTTP 403 in particular must never trigger auto-regeneration of the local
    // identity — see PublisherIdentity's header comment for why.
    private static void LogPublishFailure(long httpStatus, string body)
    {
        if (!GameFlowManager.VerboseLogging) return;

        string errorCode = null;
        try
        {
            var err = JsonUtility.FromJson<FlightPlanErrorResponse>(body);
            errorCode = err?.error;
        }
        catch
        {
            // Body wasn't JSON (e.g. a proxy error page) — fine, fall through and log the status alone.
        }

        switch (httpStatus)
        {
            case 403:
                Debug.LogWarning($"[MazeTelemetryUploader] Publish rejected (403{Suffix(errorCode)}): publisher credentials mismatch. Not regenerating identity or retrying.");
                break;
            case 409:
                Debug.LogWarning($"[MazeTelemetryUploader] Publish rejected (409{Suffix(errorCode)}): publisher flight limit reached.");
                break;
            case 429:
                Debug.LogWarning("[MazeTelemetryUploader] Publish rejected (429): rate limited. Will not retry until a future launch.");
                break;
            case 500:
                Debug.LogWarning("[MazeTelemetryUploader] Publish failed (500): server/storage error. Left pending for a future launch.");
                break;
            default:
                Debug.LogWarning($"[MazeTelemetryUploader] Publish failed (status {httpStatus}{Suffix(errorCode)}).");
                break;
        }
    }

    private static string Suffix(string errorCode) => string.IsNullOrEmpty(errorCode) ? "" : $" {errorCode}";
}
