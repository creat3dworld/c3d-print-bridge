using System.Runtime.InteropServices;

namespace C3DBridge;

// While the bridge is running, tell Windows the system is in use
// so it does not go to sleep. The screen may still turn off;
// only system sleep is prevented. The request clears automatically
// if the process exits or crashes, and is cleared explicitly on
// a clean exit, so it never sticks after the bridge stops.
public static class SleepPreventer
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public static void Prevent()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); } catch { }
    }

    public static void Allow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
    }
}
