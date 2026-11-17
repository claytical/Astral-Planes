using UnityEngine;

// Passage form (role-wide Cosmic Blocks, CosmicDustGenerator.Blocks.cs). While a role is walled,
// the few cells of that role the generator must leave carvable — ship-wide doorways, cells a ship
// overlapped at swap time, regrows that would trap something — read as plain inert gray and are
// never star food. Every cell still showing the walled role's color is a block. Role is untouched,
// so reverting is just SetPassageForm(false).
public partial class CosmicDust
{
    public bool IsPassageForm { get; private set; }

    public void SetPassageForm(bool on)
    {
        if (IsPassageForm == on) return;
        IsPassageForm = on;
        // Same as block form: hide the bud but keep HasBloomedFlower so the reverted cell gets it back.
        if (flowerRoot != null) flowerRoot.SetActive(!on && !IsBlockForm && HasBloomedFlower);
        RefreshLegibleRestTint();
    }

    // Plain gray = inert on the legibility axis (CosmicDust.Legibility.cs).
    private static Color PassageGray(Color raw)
    {
        float l = raw.r * 0.299f + raw.g * 0.587f + raw.b * 0.114f;
        return new Color(l, l, l, raw.a);
    }

    // Pool reuse / teardown.
    private void ResetPassageFormImmediate() => IsPassageForm = false;
}
