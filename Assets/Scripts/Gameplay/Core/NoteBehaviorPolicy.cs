using System.Collections.Generic;

public static class NoteBehaviorPolicy
{
    public static IReadOnlyList<NoteBehavior> MapLegacy(NoteBehavior legacy)
    {
        switch (legacy)
        {
            case NoteBehavior.Drone:      return new[] { NoteBehavior.Legatify, NoteBehavior.VelocityShape };
            case NoteBehavior.Lead:       return new[] { NoteBehavior.Staccatify, NoteBehavior.VelocityShape };
            case NoteBehavior.Percussion: return new[] { NoteBehavior.Staccatify, NoteBehavior.HumanizeTiming };
            case NoteBehavior.Glitch:     return new[] { NoteBehavior.HumanizeTiming, NoteBehavior.Swingify };
            case NoteBehavior.Harmony:    return new[] { NoteBehavior.Legatify };
            case NoteBehavior.Sustain:    return new[] { NoteBehavior.Legatify };
            case NoteBehavior.Hook:       return new[] { NoteBehavior.VelocityShape };
            case NoteBehavior.Bass:       return new[] { NoteBehavior.Staccatify, NoteBehavior.VelocityShape };
            default:                      return new[] { legacy };
        }
    }
        public static IReadOnlyList<NoteBehavior> GetDefaults(MusicalPhase phase, MusicalRole role) {
        switch (role)
        {
            case MusicalRole.Bass:
                switch (phase)
                {
                    case MusicalPhase.Establish:  return new[] { NoteBehavior.Legatify, NoteBehavior.VelocityShape };
                    case MusicalPhase.Evolve:     return new[] { NoteBehavior.Staccatify, NoteBehavior.HumanizeTiming };
                    case MusicalPhase.Intensify:  return new[] { NoteBehavior.Staccatify, NoteBehavior.DensityPulse };
                    case MusicalPhase.Release:    return new[] { NoteBehavior.Legatify };
                    case MusicalPhase.Wildcard:   return new[] { NoteBehavior.HumanizeTiming, NoteBehavior.Swingify };
                    case MusicalPhase.Pop:        return new[] { NoteBehavior.Staccatify, NoteBehavior.VelocityShape };
                    default:                      return new[] { NoteBehavior.None };
                }

            case MusicalRole.Harmony:
                switch (phase)
                {
                    case MusicalPhase.Establish:  return new[] { NoteBehavior.Legatify };
                    case MusicalPhase.Evolve:     return new[] { NoteBehavior.VelocityShape, NoteBehavior.AddNeighborOrnament };
                    case MusicalPhase.Intensify:  return new[] { NoteBehavior.InvertVoicing, NoteBehavior.DensityPulse };
                    case MusicalPhase.Release:    return new[] { NoteBehavior.Legatify, NoteBehavior.RegisterCompress };
                    case MusicalPhase.Wildcard:   return new[] { NoteBehavior.HumanizeTiming };
                    case MusicalPhase.Pop:        return new[] { NoteBehavior.VelocityShape };
                    default:                      return new[] { NoteBehavior.None };
                }

            case MusicalRole.Lead:
                switch (phase)
                {
                    case MusicalPhase.Establish:  return new[] { NoteBehavior.VelocityShape };
                    case MusicalPhase.Evolve:     return new[] { NoteBehavior.AddNeighborOrnament, NoteBehavior.HumanizeTiming };
                    case MusicalPhase.Intensify:  return new[] { NoteBehavior.Staccatify, NoteBehavior.DensityPulse };
                    case MusicalPhase.Release:    return new[] { NoteBehavior.Legatify };
                    case MusicalPhase.Wildcard:   return new[] { NoteBehavior.HumanizeTiming, NoteBehavior.Swingify };
                    case MusicalPhase.Pop:        return new[] { NoteBehavior.VelocityShape };
                    default:                      return new[] { NoteBehavior.None };
                }

            case MusicalRole.Groove:
                switch (phase)
                {
                    case MusicalPhase.Establish:  return new[] { NoteBehavior.Staccatify };
                    case MusicalPhase.Evolve:     return new[] { NoteBehavior.HumanizeTiming };
                    case MusicalPhase.Intensify:  return new[] { NoteBehavior.Staccatify, NoteBehavior.DensityPulse };
                    case MusicalPhase.Release:    return new[] { NoteBehavior.HumanizeTiming };
                    case MusicalPhase.Wildcard:   return new[] { NoteBehavior.Swingify };
                    case MusicalPhase.Pop:        return new[] { NoteBehavior.Staccatify };
                    default:                      return new[] { NoteBehavior.None };
                }

            default:
                return new[] { NoteBehavior.None };
        }
    }

    // Convenience: primary default (first item)
    public static NoteBehavior GetDefault(MusicalPhase phase, MusicalRole role)
    {
        var list = GetDefaults(phase, role);
        return (list != null && list.Count > 0) ? list[0] : NoteBehavior.None;
    }

}