using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ShipMusicalProfile", menuName = "Astral Planes/Ship Musical Profile")]
public class ShipMusicalProfile : ScriptableObject
{
    public string shipName;

    [Tooltip("List of allowed General MIDI presets for this ship")]
    public List<int> allowedMidiPresets = new List<int>();

    [Tooltip("A brief description of the ship's musical personality")]
    [TextArea(2, 4)]
    public string description;
}