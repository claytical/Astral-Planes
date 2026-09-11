using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

// Maps the existing MazeRunRecord (the complete local capture) down to the narrower Flight Plan
// publish payload. The wire format is hand-built rather than JsonUtility.ToJson'd: JsonUtility
// cannot represent a null reference field as JSON `null` (it silently omits the key instead), and
// the API contract requires a literal "solution": null for a visited-but-incomplete motif.
public static class FlightPlanPayloadBuilder
{
    public const int SchemaVersion = 1;

    // Builds the /api/publish.php body. Fails (returns false) rather than guessing when a visited
    // phaseId has no entry in TelemetryConfig.phaseIdMapping — see that mapping's comment for why
    // phase numbers can't be derived automatically from Phase.phaseId.
    public static bool TryBuildPublishJson(
        MazeRunRecord record,
        TelemetryConfig config,
        string publisherId,
        string publisherToken,
        out string json,
        out string failureReason)
    {
        json = null;
        failureReason = null;

        if (record == null) { failureReason = "record_null"; return false; }
        if (record.Path == null || record.Path.Count == 0) { failureReason = "no_path"; return false; }

        var orderedPath = record.Path.OrderBy(v => v.VisitOrder).ToList();

        var sb = new StringBuilder();
        sb.Append('{');
        AppendField(sb, "schema_version", SchemaVersion, first: true);
        AppendField(sb, "publisher_id", publisherId);
        AppendField(sb, "publisher_token", publisherToken);
        sb.Append(",\"flight\":{");
        // RunId is reused verbatim as flight_id — never mint a new one here — so retrying the
        // same local run after a failed upload is idempotent server-side.
        AppendField(sb, "flight_id", record.RunId, first: true);
        AppendField(sb, "build", Application.version);
        sb.Append(",\"steps\":[");

        for (int i = 0; i < orderedPath.Count; i++)
        {
            var visit = orderedPath[i];
            if (visit == null) continue;

            if (!config.TryGetPhaseNumber(visit.PhaseId, out int phaseNumber))
            {
                json = null;
                failureReason = $"unknown_phase_id:{visit.PhaseId}";
                return false;
            }

            if (i > 0) sb.Append(',');
            sb.Append('{');
            AppendField(sb, "phase", phaseNumber, first: true);
            AppendField(sb, "motif_id", visit.MotifId);
            sb.Append(",\"solution\":");

            var solution = FindSolutionByVisitOrder(record.Solutions, visit.VisitOrder);
            if (solution == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('{');
                AppendField(sb, "version", MotifSolutionCanonicalizer.Version, first: true);
                AppendField(sb, "canonical", MotifSolutionCanonicalizer.Canonicalize(solution));
                sb.Append('}');
            }

            sb.Append('}');
        }

        sb.Append("]}}");
        json = sb.ToString();
        return true;
    }

    public static string BuildRemoveJson(string publisherId, string publisherToken)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendField(sb, "schema_version", SchemaVersion, first: true);
        AppendField(sb, "publisher_id", publisherId);
        AppendField(sb, "publisher_token", publisherToken);
        sb.Append('}');
        return sb.ToString();
    }

    // VisitOrder, not MotifId, is the link between a path step and its solution — the same motif
    // could theoretically be visited more than once in a run, so matching by ID could attach the
    // wrong (or an ambiguous) solution.
    private static MotifSolution FindSolutionByVisitOrder(List<MotifSolution> solutions, int visitOrder)
    {
        if (solutions == null) return null;
        foreach (var s in solutions)
            if (s != null && s.VisitOrder == visitOrder) return s;
        return null;
    }

    private static void AppendField(StringBuilder sb, string key, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(key).Append("\":");
        AppendJsonString(sb, value);
    }

    private static void AppendField(StringBuilder sb, string key, int value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        if (value == null) { sb.Append("null"); return; }

        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
