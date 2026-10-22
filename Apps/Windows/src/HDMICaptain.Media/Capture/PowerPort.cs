using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.System.Threading;

namespace HDMICaptain.Media.Capture;

/// <summary>System power: keep-awake while live, and sleep/resume notifications (windows.md §2d).</summary>
public interface IPowerPort : IDisposable
{
    /// <summary>The system is about to sleep. On a system thread.</summary>
    event Action? Suspending;

    /// <summary>The system woke up. On a system thread.</summary>
    event Action? Resumed;

    /// <summary>Keeps the display on until the result is disposed.</summary>
    IDisposable RequestDisplayOn();
}

/// <summary>
/// <c>PowerRequestDisplayRequired</c> and <c>PowerRegisterSuspendResumeNotification</c> with a callback, so no hidden
/// message window is needed to hear WM_POWERBROADCAST.
/// </summary>
public sealed unsafe partial class WindowsPower : IPowerPort
{
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeSuspend = 0x7;
    private const uint PbtApmResumeAutomatic = 0x12;
    private const uint DeviceNotifyCallback = 0x2;

    /// <summary>DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS (powrprof.h).</summary>
    private struct SubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<void*, uint, void*, uint> Callback;
        public void* Context;
    }

    private readonly SubscribeParameters* _parameters;
    private readonly nint _registration;
    private int _asleep;

    public WindowsPower()
    {
        // The registration keeps the pointer for its lifetime; the callback is a static function, so no delegate to root.
        _parameters = (SubscribeParameters*)NativeMemory.Alloc((nuint)sizeof(SubscribeParameters));
        *_parameters = new SubscribeParameters
        {
            Callback = &OnPowerBroadcast,
            Context = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(this)),
        };
        if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration) != 0)
        {
            GCHandle.FromIntPtr((nint)_parameters->Context).Free();
            NativeMemory.Free(_parameters);
            throw new InvalidOperationException("PowerRegisterSuspendResumeNotification failed.");
        }
    }

    public event Action? Suspending;
    public event Action? Resumed;

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, SubscribeParameters* recipient, out nint handle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(nint handle);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint OnPowerBroadcast(void* context, uint type, void* setting)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is WindowsPower self)
        {
            switch (type)
            {
                case PbtApmSuspend:
                    Interlocked.Exchange(ref self._asleep, 1);
                    self.Suspending?.Invoke();
                    break;
                case PbtApmResumeAutomatic or PbtApmResumeSuspend:
                    // A user-initiated wake sends both; report once per sleep.
                    if (Interlocked.Exchange(ref self._asleep, 0) == 1) self.Resumed?.Invoke();
                    break;
            }
        }
        return 0;
    }

    public IDisposable RequestDisplayOn() => new DisplayRequest();

    public void Dispose()
    {
        PowerUnregisterSuspendResumeNotification(_registration); // waits for a running callback
        GCHandle.FromIntPtr((nint)_parameters->Context).Free();
        NativeMemory.Free(_parameters);
    }

    private sealed class DisplayRequest : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public DisplayRequest()
        {
            fixed (char* reason = "HDMI Captain is showing live video")
            {
                var context = new REASON_CONTEXT { Version = 0, Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING };
                context.Reason.SimpleReasonString = reason;
                _handle = PInvoke.PowerCreateRequest(context);
            }
            if (_handle.IsInvalid) return;
            PInvoke.PowerSetRequest(_handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
        }

        public void Dispose()
        {
            if (_handle.IsInvalid) return;
            PInvoke.PowerClearRequest(_handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
            _handle.Dispose();
        }
    }
}
