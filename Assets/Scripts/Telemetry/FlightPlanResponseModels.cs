using System;

// JsonUtility-safe response shapes for the Flight Plan API. Inbound-only: parsing tolerates
// missing fields (JsonUtility leaves them at their default), which is fine here since every
// field is read defensively (see MazeTelemetryUploader's strict `ok == true` success check).

[Serializable]
public class FlightPlanAggregate
{
    public int contributors;
    public int flights;
    public int motif_visits;
}

[Serializable]
public class FlightPlanPublishResponse
{
    public bool ok;
    public string publisher_id;
    public string flight_id;
    public FlightPlanAggregate aggregate;
}

[Serializable]
public class FlightPlanRemoveResponse
{
    public bool ok;
    public bool removed;
    public FlightPlanAggregate aggregate;
}

// Best-effort shape for a non-200 error body, e.g. {"error":"invalid_publisher_credentials"}.
// Never assume this parses successfully — some failure modes (a proxy 502, a truncated body)
// won't be JSON at all.
[Serializable]
public class FlightPlanErrorResponse
{
    public string error;
}
