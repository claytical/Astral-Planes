using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Steam accounts (SteamID64, as strings) excluded from gameplay analytics submission —
/// developer/test accounts whose playtime would otherwise distort the funnel. Extend by
/// adding entries to the asset; no code changes needed.
/// </summary>
[CreateAssetMenu(fileName = "SteamStatsExclusionList", menuName = "Astral Planes/Platform/Steam Stats Exclusion List")]
public class SteamStatsExclusionList : ScriptableObject
{
    public List<string> excludedSteamId64s = new();
}
