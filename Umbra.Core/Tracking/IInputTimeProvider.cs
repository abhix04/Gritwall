namespace Umbra.Core.Tracking;

/// <summary>
/// Abstraction for querying the last user input timestamp.
/// </summary>
public interface IInputTimeProvider
{
    /// <summary>
    /// Attempts to query the millisecond tick timestamp of the last keyboard or mouse input event.
    /// </summary>
    /// <param name="lastInputTick">The raw 32-bit DWORD timestamp from the input subsystem.</param>
    /// <returns>True if the query succeeded; false if the OS call failed.</returns>
    bool TryGetLastInputTick(out uint lastInputTick);
}
