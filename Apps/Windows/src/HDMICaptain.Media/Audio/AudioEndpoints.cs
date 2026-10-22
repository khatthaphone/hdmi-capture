using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace HDMICaptain.Media.Audio;

/// <summary>A WASAPI endpoint: its id (IMMDevice::GetId), friendly name and the PnP container it belongs to.</summary>
public sealed record AudioEndpoint(string Id, string Name, Guid? ContainerId);

/// <summary>
/// Audio endpoint discovery (spec §13.2, windows.md §5). A card's capture endpoint shares the ContainerId of its
/// video interface, which pairs them even with two identical cards plugged in.
/// </summary>
public static class AudioEndpoints
{
    // PKEY_Device_ContainerId = DEVPKEY_Device_ContainerId: {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}, 2.
    private static readonly PROPERTYKEY ContainerIdKey = new() { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };

    public static IReadOnlyList<AudioEndpoint> Captures() => List(EDataFlow.eCapture);

    public static IReadOnlyList<AudioEndpoint> Renders() => List(EDataFlow.eRender);

    /// <summary>The capture endpoint of the card whose video interface has <paramref name="containerId"/>.</summary>
    public static AudioEndpoint? CaptureFor(Guid containerId) => Captures().FirstOrDefault(e => e.ContainerId == containerId);

    public static AudioEndpoint? DefaultRender()
    {
        var enumerator = CreateEnumerator();
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);
            try
            {
                return Describe(device);
            }
            finally
            {
                Marshal.FinalReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            return null; // no output device at all
        }
        finally
        {
            Marshal.FinalReleaseComObject(enumerator);
        }
    }

    internal static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    internal static unsafe IMMDevice Open(IMMDeviceEnumerator enumerator, string id)
    {
        fixed (char* p = id)
        {
            enumerator.GetDevice(p, out var device);
            return device;
        }
    }

    private static List<AudioEndpoint> List(EDataFlow flow)
    {
        var enumerator = CreateEnumerator();
        try
        {
            enumerator.EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var collection);
            collection.GetCount(out var count);
            var result = new List<AudioEndpoint>((int)count);
            for (var i = 0u; i < count; i++)
            {
                collection.Item(i, out var device);
                try
                {
                    result.Add(Describe(device));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(device);
                }
            }
            Marshal.FinalReleaseComObject(collection);
            return result;
        }
        finally
        {
            Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static unsafe AudioEndpoint Describe(IMMDevice device)
    {
        PWSTR id;
        device.GetId(&id);
        var idText = id.ToString();
        PInvoke.CoTaskMemFree(id.Value);
        device.OpenPropertyStore(STGM.STGM_READ, out var store);
        try
        {
            var name = Read(store, PInvoke.PKEY_Device_FriendlyName, v => v.vt == VARENUM.VT_LPWSTR ? v.Anonymous.Anonymous.Anonymous.pwszVal.ToString() : null);
            var container = Read(store, ContainerIdKey, v => v.vt == VARENUM.VT_CLSID ? *v.Anonymous.Anonymous.Anonymous.puuid : (Guid?)null);
            return new AudioEndpoint(idText, name ?? "Audio device", container);
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    private static unsafe T? Read<T>(IPropertyStore store, PROPERTYKEY key, Func<PROPVARIANT, T?> get)
    {
        try
        {
            store.GetValue(&key, out var value);
            try
            {
                return get(value);
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }
        catch (COMException)
        {
            return default;
        }
    }
}
