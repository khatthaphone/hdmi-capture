using Windows.Win32;

namespace HDMICaptain.Media;

/// <summary>Process-wide Media Foundation lifetime: start once, shut down on exit.</summary>
public static class MediaFoundation
{
    private static int _refs;

    public static void Startup()
    {
        if (Interlocked.Increment(ref _refs) == 1)
            PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
    }

    public static void Shutdown()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
            PInvoke.MFShutdown().ThrowOnFailure();
    }
}
