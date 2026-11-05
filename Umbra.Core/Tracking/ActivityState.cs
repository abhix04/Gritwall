namespace Umbra.Core.Tracking;

/// <summary>
/// Represents the operating state of user activity for a tracking interval.
/// </summary>
public enum ActivityState
{
    /// <summary>
    /// User actively interacting with an identified foreground application.
    /// Only intervals in this state count toward application screen time.
    /// </summary>
    Active = 0,

    /// <summary>
    /// System is awake, but no user input detected for greater than or equal to the idle threshold.
    /// </summary>
    Idle = 1,

    /// <summary>
    /// Workstation is locked (e.g. SessionLock, lock screen, secure desktop).
    /// </summary>
    Locked = 2,

    /// <summary>
    /// System is active/awake, but no foreground window or desktop/shell transition exists.
    /// </summary>
    UnknownForeground = 3,

    /// <summary>
    /// Input subsystem queries failed or produced persistent unverified metrics.
    /// </summary>
    UnverifiedInput = 4
}
