using System.Runtime.InteropServices;

namespace Nightforge.Services;

/// <summary>
/// 按进程控制 Windows 音频会话的静音状态，也就是「这个游戏窗口自己有没有声音」。
/// <para>
/// 五开时五个 BGM 叠在一起，战斗提示音根本听不出来；进游戏里逐个关又很烦，
/// 而且新开的窗口默认又是开着的。这里直接在系统层面按进程静音，一次搞定，
/// 什么时候想听再一键恢复，不需要动游戏里的设置。
/// </para>
/// <para>
/// 只用 Core Audio 的会话接口（IMSimpleAudioVolume），不碰系统主音量，
/// 所以静音游戏不会影响你看视频、听歌。
/// </para>
/// </summary>
public sealed class AudioService
{
    // ---------- COM 常量 ----------
    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private const int EDataFlowRender = 0;
    private const int DeviceStateActive = 0x00000001;
    private const uint ClsCtxAll = 23;

    /// <summary>
    /// 把指定进程的音频会话设成静音 / 取消静音。
    /// <paramref name="pids"/> 之外的会话一个都不碰（不会误关浏览器、音乐播放器的声音）。
    /// </summary>
    /// <returns>实际处理成功的会话数。</returns>
    public int SetMute(IReadOnlyCollection<uint> pids, bool mute, out string message)
    {
        message = string.Empty;
        var targets = new HashSet<uint>(pids.Where(p => p != 0));
        if (targets.Count == 0)
        {
            return 0;
        }

        int done = 0;
        try
        {
            IMMDeviceEnumerator? enumerator = CreateEnumerator();
            if (enumerator is null)
            {
                message = "系统音频服务不可用";
                return 0;
            }

            if (enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out IMMDeviceCollection devices) != 0
                || devices is null)
            {
                message = "读不到音频输出设备";
                return 0;
            }

            devices.GetCount(out int deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out IMMDevice device) != 0 || device is null)
                {
                    continue;
                }

                Guid iid = IidAudioSessionManager2;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object activated) != 0)
                {
                    continue;
                }

                if (activated is not IAudioSessionManager2 manager
                    || manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0
                    || sessions is null)
                {
                    continue;
                }

                sessions.GetCount(out int sessionCount);
                for (int s = 0; s < sessionCount; s++)
                {
                    if (sessions.GetSession(s, out IAudioSessionControl2 session) != 0 || session is null)
                    {
                        continue;
                    }

                    if (session.GetProcessId(out uint pid) != 0 || !targets.Contains(pid))
                    {
                        continue;
                    }

                    if (session is not ISimpleAudioVolume volume)
                    {
                        continue;
                    }

                    if (volume.SetMute(mute, IntPtr.Zero) == 0)
                    {
                        done++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        return done;
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        try
        {
            Type? type = Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator);
            return type is null ? null : Activator.CreateInstance(type) as IMMDeviceEnumerator;
        }
        catch
        {
            return null;
        }
    }

    // ---------- COM 接口声明（顺序即 vtable 顺序，不能乱） ----------

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr session);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out ISimpleAudioVolume volume);

        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterSessionNotification(IntPtr client);
        [PreserveSig] int RegisterDuckNotification(IntPtr client);
        [PreserveSig] int UnregisterDuckNotification(IntPtr client);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
