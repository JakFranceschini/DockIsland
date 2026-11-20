using System;
using System.Runtime.InteropServices;

namespace DockIsland;

// Declarações mínimas da Core Audio API (sem dependências externas).
// A ordem dos métodos segue exatamente a vtable COM; os não usados são só "placeholders".

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                  [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IntPtr properties);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    void GetChannelCount(out uint count);
    void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    void GetMasterVolumeLevel(out float levelDb);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    void GetChannelVolumeLevel(uint channel, out float levelDb);
    void GetChannelVolumeLevelScalar(uint channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

internal static class AudioVolume
{
    // Obtém o dispositivo de saída padrão atual (ele muda ao plugar fone, por ex.),
    // executa a ação e libera os objetos COM.
    static bool With(Action<IAudioEndpointVolume> action)
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDevice device = null;
        IAudioEndpointVolume vol = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device);
            if (device == null) return false;

            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o);
            vol = (IAudioEndpointVolume)o;
            action(vol);
            return true;
        }
        catch { return false; }
        finally
        {
            if (vol != null) Marshal.ReleaseComObject(vol);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public static bool TryGet(out float level, out bool muted)
    {
        float l = 0; bool m = false;
        bool ok = With(v =>
        {
            v.GetMasterVolumeLevelScalar(out l);
            v.GetMute(out m);
        });
        level = l; muted = m;
        return ok;
    }

    public static bool SetLevel(float level)
    {
        level = Math.Max(0f, Math.Min(1f, level));
        return With(v =>
        {
            var g = Guid.Empty;
            v.SetMasterVolumeLevelScalar(level, ref g);
            if (level > 0f) v.SetMute(false, ref g);   // mexer no volume tira do mudo, como o Windows
        });
    }

    public static bool SetMute(bool mute) => With(v =>
    {
        var g = Guid.Empty;
        v.SetMute(mute, ref g);
    });
}
