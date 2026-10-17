using System.Runtime.InteropServices;

namespace PureKBridge.App.Services.NativePublishing;

[ComImport, Guid("204810b6-73b2-11d4-bf42-00b0d0118b56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeUpnpRegistrar
{
    [return: MarshalAs(UnmanagedType.BStr)] string RegisterDevice([MarshalAs(UnmanagedType.BStr)] string xml,
        [MarshalAs(UnmanagedType.BStr)] string progId, [MarshalAs(UnmanagedType.BStr)] string init,
        [MarshalAs(UnmanagedType.BStr)] string container, [MarshalAs(UnmanagedType.BStr)] string resources, int lifetime);
    [return: MarshalAs(UnmanagedType.BStr)] string RegisterRunningDevice([MarshalAs(UnmanagedType.BStr)] string xml,
        [MarshalAs(UnmanagedType.IUnknown)] object control, [MarshalAs(UnmanagedType.BStr)] string init,
        [MarshalAs(UnmanagedType.BStr)] string resources, int lifetime);
    void RegisterDeviceProvider([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.BStr)] string progId,
        [MarshalAs(UnmanagedType.BStr)] string init, [MarshalAs(UnmanagedType.BStr)] string container);
    [return: MarshalAs(UnmanagedType.BStr)] string GetUniqueDeviceName([MarshalAs(UnmanagedType.BStr)] string identifier,
        [MarshalAs(UnmanagedType.BStr)] string templateUdn);
    void UnregisterDevice([MarshalAs(UnmanagedType.BStr)] string identifier, [MarshalAs(UnmanagedType.Bool)] bool permanent);
    void UnregisterDeviceProvider([MarshalAs(UnmanagedType.BStr)] string name);
}

[ComVisible(true), Guid("204810ba-73b2-11d4-bf42-00b0d0118b56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeUpnpDeviceControl
{
    void Initialize([MarshalAs(UnmanagedType.BStr)] string xml, [MarshalAs(UnmanagedType.BStr)] string identifier,
        [MarshalAs(UnmanagedType.BStr)] string init);
    [return: MarshalAs(UnmanagedType.IDispatch)] object GetServiceObject([MarshalAs(UnmanagedType.BStr)] string udn,
        [MarshalAs(UnmanagedType.BStr)] string serviceId);
}

[ComVisible(true), Guid("204810b5-73b2-11d4-bf42-00b0d0118b56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeUpnpEventSource
{
    void Advise([MarshalAs(UnmanagedType.IUnknown)] object sink);
    void Unadvise([MarshalAs(UnmanagedType.IUnknown)] object sink);
}

[ComImport, Guid("204810b4-73b2-11d4-bf42-00b0d0118b56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeUpnpEventSink
{
    void OnStateChanged(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] changes);
    void OnStateChangedSafe([MarshalAs(UnmanagedType.Struct)] object changes);
}

internal static class NativeUpnp
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeSecurity(IntPtr security, int count, IntPtr services, IntPtr reserved,
        uint authentication, uint impersonation, IntPtr authList, uint capabilities, IntPtr reserved3);
    internal static int ConfigureProcessSecurity() => CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero,
        IntPtr.Zero, 0, 3, IntPtr.Zero, 0, IntPtr.Zero);
    internal static INativeUpnpRegistrar CreateRegistrar() => (INativeUpnpRegistrar)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("204810b9-73b2-11d4-bf42-00b0d0118b56"), true)!)!;

}
