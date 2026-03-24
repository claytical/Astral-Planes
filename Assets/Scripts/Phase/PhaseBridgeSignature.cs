using System;
using UnityEngine;
//CHATGPT REMOVAL HICCUP - NOT CURRENTLY INTEGRATED
public enum HarmonyCommit { AtBridgeStart, MidBridge, AtBridgeEnd }

[Serializable]
public class PhaseBridgeSignature
{
    
    [Header("Harmony handoff")]
    public HarmonyCommit commitTiming = HarmonyCommit.AtBridgeEnd;


}

public static class BridgeLibrary
{
    public static PhaseBridgeSignature Default() => new PhaseBridgeSignature {
        commitTiming = HarmonyCommit.AtBridgeEnd
    };
    
}
