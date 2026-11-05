using System.Runtime.InteropServices;

namespace Umbra.Core.Tracking;

/// <summary>
/// Production implementation of IInputTimeProvider calling user32!GetLastInputInfo.
/// </summary>
public sealed class WindowsInputTimeProvider : IInputTimeProvider
{
    public bool TryGetLastInputTick(out uint lastInputTick)
    {
        var lii = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };

        if (NativeMethods.GetLastInputInfo(ref lii))
        {
            lastInputTick = lii.dwTime;
            return true;
        }

        lastInputTick = 0;
        return false;
    }
}
