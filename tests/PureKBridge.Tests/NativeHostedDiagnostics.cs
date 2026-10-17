using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace PureKBridge.Tests;

[ComImport, Guid("204810b6-73b2-11d4-bf42-00b0d0118b56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITestUpnpRegistrar
{
    [return: MarshalAs(UnmanagedType.BStr)]
    string RegisterDevice([MarshalAs(UnmanagedType.BStr)] string xml, [MarshalAs(UnmanagedType.BStr)] string progId,
        [MarshalAs(UnmanagedType.BStr)] string init, [MarshalAs(UnmanagedType.BStr)] string container,
        [MarshalAs(UnmanagedType.BStr)] string resources, int lifetime);
    [return: MarshalAs(UnmanagedType.BStr)]
    string RegisterRunningDevice([MarshalAs(UnmanagedType.BStr)] string xml, [MarshalAs(UnmanagedType.IUnknown)] object? control,
        [MarshalAs(UnmanagedType.BStr)] string init, [MarshalAs(UnmanagedType.BStr)] string resources, int lifetime);
    void RegisterDeviceProvider([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.BStr)] string progId,
        [MarshalAs(UnmanagedType.BStr)] string init, [MarshalAs(UnmanagedType.BStr)] string container);
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetUniqueDeviceName([MarshalAs(UnmanagedType.BStr)] string identifier, [MarshalAs(UnmanagedType.BStr)] string templateUdn);
    void UnregisterDevice([MarshalAs(UnmanagedType.BStr)] string identifier, [MarshalAs(UnmanagedType.Bool)] bool permanent);
    void UnregisterDeviceProvider([MarshalAs(UnmanagedType.BStr)] string name);
}

[ComVisible(true), Guid("204810ba-73b2-11d4-bf42-00b0d0118b56"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITestHostedDeviceControl
{
    void Initialize([MarshalAs(UnmanagedType.BStr)] string xml, [MarshalAs(UnmanagedType.BStr)] string identifier,
        [MarshalAs(UnmanagedType.BStr)] string init);
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object GetServiceObject([MarshalAs(UnmanagedType.BStr)] string udn, [MarshalAs(UnmanagedType.BStr)] string serviceId);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class TestHostedDeviceControl : ITestHostedDeviceControl
{
    public void Initialize(string xml, string identifier, string init) { }
    public object GetServiceObject(string udn, string serviceId) => throw new COMException("This test device has no services.", unchecked((int)0x80004001));
}

/// <summary>Isolated native publishing experiment. No proxy/control, driver, COM class registration or permanent device.</summary>
internal static class NativeHostedDiagnostics
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeSecurity(IntPtr security, int count, IntPtr services, IntPtr reserved,
        uint authentication, uint impersonation, IntPtr authList, uint capabilities, IntPtr reserved3);
    public static int SecurityResult { get; private set; }
    public static void InitializeSecurity() => SecurityResult = CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero,
        IntPtr.Zero, 0, 3, IntPtr.Zero, 0, IntPtr.Zero);

    public static async Task RunAsync(string artifacts)
    {
        object? instance = null;
        ITestUpnpRegistrar? registrar = null;
        string? identifier = null, actualUdn = null, error = null, cleanupError = null;
        var unregistered = false;
        var nativeFound = false;
        string? errorDetail = null;
        string[] events = [];
        var control = new TestHostedDeviceControl();
        var administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var templateUdn = "uuid:" + Guid.NewGuid();
        // No services: this experiment only tests native publication and local visibility.
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
              <device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>ChunK temporary discovery probe</friendlyName><manufacturer>ChunK tests</manufacturer>
                <modelName>Discovery only</modelName><UDN>{templateUdn}</UDN></device>
            </root>
            """;
        Console.WriteLine($"Native host probe: administrator={administrator}, CoInitializeSecurity=0x{SecurityResult:X8}");
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("204810b9-73b2-11d4-bf42-00b0d0118b56"), true)!;
            instance = Activator.CreateInstance(type)!;
            registrar = (ITestUpnpRegistrar)instance;
            identifier = registrar.RegisterRunningDevice(xml, control, "", artifacts, 900);
            actualUdn = registrar.GetUniqueDeviceName(identifier, templateUdn);
            Console.WriteLine("Native temporary device registered: " + actualUdn);
            using var observer = new NativeDiscoveryProbe(actualUdn);
            try { await observer.Callback.Found.Task.WaitAsync(TimeSpan.FromSeconds(15)); nativeFound = true; }
            catch (TimeoutException) { }
            events = observer.Callback.Events;
            Console.WriteLine("Native local discovery=" + nativeFound + "; " + string.Join("; ", events));
        }
        catch (Exception failure) { error = failure.GetType().Name + " 0x" + failure.HResult.ToString("X8") + ": " + failure.Message; errorDetail = failure.ToString(); Console.WriteLine(errorDetail); }
        finally
        {
            if (registrar is not null && identifier is not null)
            {
                try { registrar.UnregisterDevice(identifier, true); unregistered = true; Console.WriteLine("Temporary native device unregistered."); }
                catch (Exception failure) { cleanupError = failure.GetType().Name + ": " + failure.Message; }
            }
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            GC.KeepAlive(control);
            var path = Path.Combine(artifacts, "native-host-diagnostics.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, administrator,
                securityResult = "0x" + SecurityResult.ToString("X8"), templateUdn, identifier, actualUdn,
                nativeFound, events, error, errorDetail, unregistered, cleanupError,
                note = "Test-only RegisterRunningDevice, minimum 900-second announcement lifetime. No service/control implementation, real device authorization, permanent registration, driver, firewall or network changes. No explicit service start/stop." },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Report: " + path);
        }
    }
}
