using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PureKBridge.App.Services.NativePublishing;

namespace PureKBridge.Tests;

internal static class HostedServiceProbe
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TypeCount(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TypeInfo(IntPtr self, uint index, uint locale, out IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Names(IntPtr self, ref Guid iid, IntPtr names, uint count, uint locale, out int id);
    public const string Scpd = """
        <scpd xmlns="urn:schemas-upnp-org:service-1-0"><specVersion><major>1</major><minor>0</minor></specVersion>
          <actionList><action><name>Echo</name><argumentList>
            <argument><name>Input</name><direction>in</direction><relatedStateVariable>Value</relatedStateVariable></argument>
            <argument><name>Output</name><direction>out</direction><relatedStateVariable>Value</relatedStateVariable></argument>
          </argumentList></action></actionList>
          <serviceStateTable><stateVariable sendEvents="yes"><name>Value</name><dataType>string</dataType><defaultValue>ready</defaultValue></stateVariable></serviceStateTable>
        </scpd>
        """;
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Control(object service) : INativeUpnpDeviceControl
    {
        public void Initialize(string xml, string identifier, string init) { Console.WriteLine("Device control Initialize: " + identifier); }
        public object GetServiceObject(string udn, string serviceId) { Console.WriteLine("GetServiceObject: " + serviceId); return service; }
    }
    public static async Task RunAsync(string artifacts)
    {
        var schema = ServiceSchema.Parse(Scpd);
        var calls = new List<string>();
        var value = "ready";
        var state = ServiceDispatchFactory.Create(schema, (action, args) => { calls.Add(action); value = "echo:" + args[0]; return [value]; }, _ => value);
        state.EventFailed = failure => Console.WriteLine("Event sink failure: " + failure);
        var dispatch = Marshal.GetIDispatchForObject(state);
        try
        {
            var table = Marshal.ReadIntPtr(dispatch);
            var countResult = Marshal.GetDelegateForFunctionPointer<TypeCount>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size))(dispatch, out var count);
            var typeResult = Marshal.GetDelegateForFunctionPointer<TypeInfo>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size))(dispatch, 0, 0, out var info);
            Console.WriteLine($"Dispatch type info count={count}, countHRESULT=0x{countResult:X8}, typeHRESULT=0x{typeResult:X8}");
            if (info != IntPtr.Zero) Marshal.Release(info);
            foreach (var name in new[] { "Echo", "Value" })
            {
                var text = Marshal.StringToCoTaskMemUni(name); var array = Marshal.AllocCoTaskMem(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(array, text); var iid = Guid.Empty;
                    var result = Marshal.GetDelegateForFunctionPointer<Names>(Marshal.ReadIntPtr(table, 5 * IntPtr.Size))(dispatch, ref iid, array, 1, 0, out var dispid);
                    Console.WriteLine($"Dispatch {name}: id={dispid}, HRESULT=0x{result:X8}");
                }
                finally { Marshal.FreeCoTaskMem(array); Marshal.FreeCoTaskMem(text); }
            }
        }
        finally { Marshal.Release(dispatch); }
        var control = new Control(state);
        var directory = Path.Combine(artifacts, "hosted-service-probe");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "echo.xml"), Scpd);
        var registrar = NativeUpnp.CreateRegistrar();
        string? id = null, udn = null, error = null, documentUrl = null, response = null;
        var found = false; var unregistered = false; var initialEvent = false; var changedEvent = false;
        try
        {
            var template = "uuid:" + Guid.NewGuid();
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <root xmlns="urn:schemas-upnp-org:device-1-0"><specVersion><major>1</major><minor>0</minor></specVersion>
                <device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>ChunK service probe</friendlyName>
                <manufacturer>ChunK tests</manufacturer><modelName>Echo</modelName><UDN>{template}</UDN>
                <serviceList><service><serviceType>urn:schemas-upnp-org:service:Echo:1</serviceType><serviceId>urn:upnp-org:serviceId:Echo</serviceId>
                <SCPDURL>echo.xml</SCPDURL><controlURL></controlURL><eventSubURL></eventSubURL></service></serviceList></device></root>
                """;
            id = registrar.RegisterRunningDevice(xml, control, "", directory, 900);
            udn = registrar.GetUniqueDeviceName(id, template);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };
            // The loopback Device Host URL was obtained from the preceding successful native-host probe.
            var published = "http://127.0.0.1:2869/upnphost/udhisapi.dll?content=" + udn;
            var publishedXml = await http.GetStringAsync(published);
            await File.WriteAllTextAsync(Path.Combine(directory, "published.xml"), publishedXml);
            var directDevice = ServiceSchema.ReadXml(publishedXml);
            var scpdPath = directDevice.Descendants().Single(x => x.Name.LocalName == "SCPDURL").Value;
            using var scpdResponse = await http.GetAsync(new Uri(new Uri(published), scpdPath));
            var publishedScpd = await scpdResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Published SCPD HTTP {(int)scpdResponse.StatusCode}: {publishedScpd}");
            var loaded = await Task.Run(() => DiscoveryDiagnostics.LoadDescription(new Uri(published))).WaitAsync(TimeSpan.FromSeconds(12));
            Console.WriteLine("Direct native description: " + JsonSerializer.Serialize(loaded));
            using var observer = new NativeDiscoveryProbe(udn);
            try { await observer.Callback.Found.Task.WaitAsync(TimeSpan.FromSeconds(12)); found = true; }
            catch (TimeoutException) { Console.WriteLine("Discovery timed out; testing published control endpoint independently."); }
            documentUrl = found ? await observer.Callback.DocumentUrl.Task.WaitAsync(TimeSpan.FromSeconds(2)) : published;
            var deviceXml = ServiceSchema.ReadXml(await http.GetStringAsync(documentUrl));
            var controlPath = deviceXml.Descendants().Single(x => x.Name.LocalName == "controlURL").Value;
            var soap = "<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/' s:encodingStyle='http://schemas.xmlsoap.org/soap/encoding/'><s:Body><u:Echo xmlns:u='urn:schemas-upnp-org:service:Echo:1'><Input>hello</Input></u:Echo></s:Body></s:Envelope>";
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(documentUrl), controlPath)) { Content = new StringContent(soap, Encoding.UTF8, "text/xml") };
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"urn:schemas-upnp-org:service:Echo:1#Echo\"");
            using var result = await http.SendAsync(request);
            response = await result.Content.ReadAsStringAsync();
            Console.WriteLine($"Native service HTTP {(int)result.StatusCode}: {response}");
            if (!result.IsSuccessStatusCode || !response.Contains("echo:hello")) throw new InvalidOperationException("Generated native service call failed.");
            await using var callback = new FakeRenderer();
            callback.Handler = _ => Task.FromResult(new TestResponse(200, ""));
            var eventPath = deviceXml.Descendants().Single(x => x.Name.LocalName == "eventSubURL").Value;
            var eventUrl = new Uri(new Uri(documentUrl), eventPath);
            using var subscribe = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), eventUrl);
            subscribe.Headers.TryAddWithoutValidation("CALLBACK", "<" + new Uri(callback.BaseUrl, "events") + ">");
            subscribe.Headers.TryAddWithoutValidation("NT", "upnp:event");
            subscribe.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
            using var subscribed = await http.SendAsync(subscribe);
            Console.WriteLine("Native SUBSCRIBE: " + (int)subscribed.StatusCode + "; has sink=" + state.HasSubscriber);
            subscribed.EnsureSuccessStatusCode();
            for (var i = 0; i < 100 && !callback.Requests.Any(r => r.Method == "NOTIFY"); i++) await Task.Delay(20);
            initialEvent = callback.Requests.Any(r => r.Method == "NOTIFY" && r.Body.Contains("echo:hello"));
            value = "changed"; state.Changed([1]);
            for (var i = 0; i < 100 && !callback.Requests.Any(r => r.Body.Contains("changed")); i++) await Task.Delay(20);
            changedEvent = callback.Requests.Any(r => r.Method == "NOTIFY" && r.Body.Contains("changed"));
            using var unsubscribe = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), eventUrl);
            unsubscribe.Headers.TryAddWithoutValidation("SID", subscribed.Headers.GetValues("SID").Single());
            using var unsubscribed = await http.SendAsync(unsubscribe);
            Console.WriteLine($"Native events: initial={initialEvent}, changed={changedEvent}, unsubscribe={(int)unsubscribed.StatusCode}");
            if (!initialEvent || !changedEvent || !unsubscribed.IsSuccessStatusCode) throw new InvalidOperationException("Native event delivery failed.");
        }
        catch (Exception failure) { error = failure.ToString(); Console.WriteLine(error); }
        finally
        {
            if (id is not null) { registrar.UnregisterDevice(id, true); unregistered = true; }
            state.Close(); Marshal.FinalReleaseComObject(registrar); GC.KeepAlive(control);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "hosted-service-probe.json"), JsonSerializer.Serialize(
                new { timestamp = DateTimeOffset.Now, found, udn, documentUrl, calls, response, initialEvent, changedEvent, error, unregistered }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
