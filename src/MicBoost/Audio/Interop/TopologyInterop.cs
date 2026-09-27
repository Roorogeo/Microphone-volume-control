using System.Runtime.InteropServices;

namespace MicBoost.Audio.Interop;

// ---------------------------------------------------------------------------------------
// Minimal COM declarations for the Windows Device Topology API (devicetopology.h).
//
// NAudio covers the endpoint side of Core Audio well, but to find and drive the
// "Microphone Boost" control we need the raw IPart / IAudioVolumeLevel interfaces,
// including level ranges, step sizes and control-change callbacks. The vtable order of
// every interface below must match the SDK header exactly - do not reorder methods.
// ---------------------------------------------------------------------------------------

internal static class ComIids
{
    public static readonly Guid IDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    public static readonly Guid IAudioVolumeLevel = new("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC");
    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>CLSCTX_ALL (INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER).</summary>
    public const uint ClsCtxAll = 0x17;

    /// <summary>HRESULT E_NOTFOUND - returned e.g. when a part has no incoming parts.</summary>
    public const int E_NOTFOUND = unchecked((int)0x80070490);
}

internal enum PartType
{
    Connector = 0,
    Subunit = 1,
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorRaw
{
    // Only GetDevice is used; the first two slots are declared to keep the vtable aligned.
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceRaw device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceRaw
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    [PreserveSig] int GetConnectorCount(out uint count);
    [PreserveSig] int GetConnector(uint index, out IConnector connector);
    [PreserveSig] int GetSubunitCount(out uint count);
    [PreserveSig] int GetSubunit(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object subunit);
    [PreserveSig] int GetPartById(uint id, out IPart part);
    [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
    [PreserveSig] int GetSignalPath(IPart from, IPart to, [MarshalAs(UnmanagedType.Bool)] bool rejectMixedPaths, out IPartsList parts);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    [PreserveSig] int GetConnectorType(out int type);
    [PreserveSig] int GetDataFlow(out int flow);
    [PreserveSig] int ConnectTo(IConnector connectTo);
    [PreserveSig] int Disconnect();
    [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    [PreserveSig] int GetConnectedTo(out IConnector connectedTo);
    [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string connectorId);
    [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
}

[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int GetLocalId(out uint id);
    [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string globalId);
    [PreserveSig] int GetPartType(out PartType partType);
    [PreserveSig] int GetSubType(out Guid subType);
    [PreserveSig] int GetControlInterfaceCount(out uint count);
    [PreserveSig] int GetControlInterface(uint index, out IControlInterface control);
    [PreserveSig] int EnumPartsIncoming(out IPartsList parts);
    [PreserveSig] int EnumPartsOutgoing(out IPartsList parts);
    [PreserveSig] int GetTopologyObject(out IDeviceTopology topology);
    [PreserveSig] int Activate(uint clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int RegisterControlChangeCallback(ref Guid iid, IControlChangeNotify notify);
    [PreserveSig] int UnregisterControlChangeCallback(IControlChangeNotify notify);
}

[ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPartsList
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetPart(uint index, out IPart part);
}

[ComImport, Guid("45D37C3F-5140-444A-AE24-400789F3CBF3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IControlInterface
{
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int GetIID(out Guid iid);
}

/// <summary>
/// IAudioVolumeLevel derives from IPerChannelDbLevel without adding methods, so all
/// IPerChannelDbLevel methods are declared here in their original order.
/// </summary>
[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioVolumeLevel
{
    [PreserveSig] int GetChannelCount(out uint channels);
    [PreserveSig] int GetLevelRange(uint channel, out float minLevelDb, out float maxLevelDb, out float stepping);
    [PreserveSig] int GetLevel(uint channel, out float levelDb);
    [PreserveSig] int SetLevel(uint channel, float levelDb, ref Guid eventContext);
    [PreserveSig] int SetLevelUniform(float levelDb, ref Guid eventContext);
    [PreserveSig] int SetLevelAllChannels([MarshalAs(UnmanagedType.LPArray)] float[] levelsDb, uint channels, ref Guid eventContext);
}

[ComImport, Guid("A09513ED-C709-4D21-BD7B-5F34C47F3947"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IControlChangeNotify
{
    [PreserveSig] int OnNotify(uint senderProcessId, IntPtr eventContext);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}
