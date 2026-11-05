namespace Umbra.Core.Tracking;

/// <summary>
/// Represents the lifecycle completion status of a tracking interval.
/// </summary>
public enum IntervalStatus
{
    /// <summary>
    /// The interval is actively accumulating time and has not been finalized.
    /// </summary>
    Open = 0,

    /// <summary>
    /// The interval was cleanly finalized upon a verified state transition or shutdown.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// The interval was left unclosed due to an unclean process crash or power loss,
    /// and was bounded to its last confirmed heartbeat on startup recovery.
    /// </summary>
    TruncatedByCrash = 2,

    /// <summary>
    /// The interval was left unclosed across a system sleep/hibernation event where
    /// an OS suspend broadcast was missed, bounded on resume reconciliation.
    /// </summary>
    TruncatedByUnnotifiedSleep = 3
}
