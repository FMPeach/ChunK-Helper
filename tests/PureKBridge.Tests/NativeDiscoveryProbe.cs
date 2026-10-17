using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace PureKBridge.Tests;

[ComImport, Guid("E7772804-3287-418E-9072-CF2B47238981"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITestDeviceDocumentAccess
{
    [return: MarshalAs(UnmanagedType.BStr)] string GetDocumentURL();
}

// Test-only COM observer; ABI definitions match Windows SDK upnp.h.
[ComVisible(true), Guid("415A984A-88B3-49F3-92AF-0508BEDF0D6C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITestDeviceFinderCallback
{
    void DeviceAdded(int search, [MarshalAs(UnmanagedType.IDispatch)] object device);
    void DeviceRemoved(int search, [MarshalAs(UnmanagedType.BStr)] string udn);
    void SearchComplete(int search);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class TestDeviceFinderCallback(string expectedUdn) : ITestDeviceFinderCallback
{
    private readonly ConcurrentQueue<string> _events = new();
    public string[] Events => _events.ToArray();
    public TaskCompletionSource Found { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Removed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> DocumentUrl { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void DeviceAdded(int search, object device)
    {
        try
        {
            var udn = (string)((dynamic)device).UniqueDeviceName;
            _events.Enqueue("DeviceAdded: " + udn);
            if (udn.Equals(expectedUdn, StringComparison.OrdinalIgnoreCase)) Found.TrySetResult();
            if (udn.Equals(expectedUdn, StringComparison.OrdinalIgnoreCase))
            {
                try { var url = ((ITestDeviceDocumentAccess)device).GetDocumentURL(); _events.Enqueue("DocumentURL: " + url); DocumentUrl.TrySetResult(url); }
                catch (Exception error) { _events.Enqueue("DocumentURL error: " + error.GetType().Name + " " + error.HResult.ToString("X8")); }
            }
        }
        catch (Exception error) { _events.Enqueue("DeviceAdded error: " + error.GetType().Name + " " + error.HResult.ToString("X8")); }
        // The UPnP API forbids releasing the notified device inside the callback.
        // Let the temporary RCW be collected after returning instead.
    }
    public void DeviceRemoved(int search, string udn)
    { _events.Enqueue("DeviceRemoved: " + udn); if (udn.Equals(expectedUdn, StringComparison.OrdinalIgnoreCase)) Removed.TrySetResult(); }
    public void SearchComplete(int search) => _events.Enqueue("SearchComplete");
}

internal sealed class NativeDiscoveryProbe : IDisposable
{
    private readonly object _finder;
    private readonly int _search;
    public TestDeviceFinderCallback Callback { get; }

    public NativeDiscoveryProbe(string udn)
    {
        var type = Type.GetTypeFromProgID("UPnP.UPnPDeviceFinder") ?? throw new NotSupportedException("Windows UPnP Finder unavailable");
        _finder = Activator.CreateInstance(type)!;
        Callback = new(udn);
        try
        {
            _search = (int)((dynamic)_finder).CreateAsyncFind("urn:schemas-upnp-org:device:MediaRenderer:1", 0, Callback);
            ((dynamic)_finder).StartAsyncFind(_search);
        }
        catch { Marshal.FinalReleaseComObject(_finder); throw; }
    }

    public void Dispose()
    {
        try { ((dynamic)_finder).CancelAsyncFind(_search); }
        catch (COMException) { }
        finally { Marshal.FinalReleaseComObject(_finder); GC.KeepAlive(Callback); }
    }
}
