using System;

// Identity for a single voice within a role: (MusicalRole, voiceIndex). The implicit conversion
// from MusicalRole (voiceIndex = 0) lets any existing call site that only knows a bare role
// compile unchanged against a RoleVoiceKey-typed parameter — voice 0 is today's only voice for
// every currently-shipped motif.
public readonly struct RoleVoiceKey : IEquatable<RoleVoiceKey>
{
    public readonly MusicalRole role;
    public readonly int voiceIndex;

    public RoleVoiceKey(MusicalRole role, int voiceIndex)
    {
        this.role = role;
        this.voiceIndex = voiceIndex;
    }

    public bool Equals(RoleVoiceKey other) => role == other.role && voiceIndex == other.voiceIndex;
    public override bool Equals(object obj) => obj is RoleVoiceKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(role, voiceIndex);
    public override string ToString() => voiceIndex == 0 ? role.ToString() : $"{role}#{voiceIndex}";

    public static implicit operator RoleVoiceKey(MusicalRole role) => new RoleVoiceKey(role, 0);
}
