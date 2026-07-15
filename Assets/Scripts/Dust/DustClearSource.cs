/// <summary>
/// Identifies what removed a dust cell, so regrow delay can be tuned per source
/// via <see cref="DustTimingParams"/> on the active MazePatternConfig.
/// </summary>
public enum DustClearSource
{
    System = 0,          // internal maze ops / unspecified — always uses the base delay
    VehiclePlow,
    StarDrain,           // held by the MineNode built from the drained energy; delay applies on release
    Zap,                 // non-held zap fallback
    CollectableArrival,
    CollectablePlow,
    Jail,
    SuperNode,
}
