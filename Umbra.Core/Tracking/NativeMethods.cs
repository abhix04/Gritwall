using System.Runtime.InteropServices;

namespace Umbra.Core.Tracking;

/// <summary>
/// Low-level abstraction for native OS clock sources to enable deterministic unit testing
/// of preemption, clock sampling rejections, and native API failures.
/// </summary>
public interface INativeClockSource
{
    uint GetTickCount();
    ulong GetTickCount64();
    bool QueryUnbiasedInterruptTime(out ulong lpUnbiasedInterruptTime);
    DateTimeOffset GetUtcNow();
}

internal sealed class DefaultNativeClockSource : INativeClockSource
{
    public static readonly DefaultNativeClockSource Instance = new();

    public uint GetTickCount() => NativeMethods.GetTickCount();
    public ulong GetTickCount64() => NativeMethods.GetTickCount64();
    public bool QueryUnbiasedInterruptTime(out ulong lpUnbiasedInterruptTime) =>
        NativeMethods.QueryUnbiasedInterruptTime(out lpUnbiasedInterruptTime);
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryUnbiasedInterruptTime(out ulong lpUnbiasedInterruptTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
