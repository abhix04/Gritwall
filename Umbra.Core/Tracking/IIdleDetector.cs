namespace Umbra.Core.Tracking;

/// <summary>
/// Contract for detecting user inactivity and tracking input timestamp progression.
/// </summary>
public interface IIdleDetector
{
    TimeSpan IdleThreshold { get; set; }

    /// <summary>
    /// Resets the input epoch following an explicit transition (e.g. system resume or workstation unlock).
    /// </summary>
    void ResetEpoch(ClockReading reading);

    /// <summary>
    /// Evaluates current idle state against the configured threshold.
    /// </summary>
    /// <returns>True if an evaluation was successfully performed; false if input query failed.</returns>
    bool TryCheckIdle(ClockReading reading, out IdleCheckResult result);
}
