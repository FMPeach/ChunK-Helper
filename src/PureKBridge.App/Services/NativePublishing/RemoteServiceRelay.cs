using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using PureKBridge.Core.Models;

namespace PureKBridge.App.Services.NativePublishing;

internal sealed class RemoteServiceRelay : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop;
    private readonly Action<Exception> _failed;
    private readonly object _sync = new();
    private readonly Dictionary<string, object> _values;
    private readonly TaskCompletionSource _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _received = new(StringComparer.Ordinal);
    private string? _sid;
    private (string Sid, IReadOnlyDictionary<string, object> Values)? _pending;
    private uint? _lastSequence;
    private Task? _renewal;
    private bool _disposed;
    public RendererService Service { get; }
    public ServiceSchema Schema { get; }
    public Action<int[]>? Changed { get; set; }

    public RemoteServiceRelay(RendererService service, ServiceSchema schema, HttpClient http,
        CancellationToken cancellation, Action<Exception> failed)
    {
        Service = service; Schema = schema; _http = http; _failed = failed;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _values = schema.Variables.ToDictionary(v => v.Name, v => v.Default, StringComparer.Ordinal);
        if (!schema.Variables.Any(v => v.Evented)) _initial.TrySetResult();
    }
    public object Read(string name) { lock (_sync) return _values[name]; }

    public object?[] Invoke(string name, object?[] inputs)
    {
        try { return InvokeAsync(name, inputs).GetAwaiter().GetResult(); }
        catch (COMException) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException or System.Xml.XmlException or FormatException or OverflowException)
        { throw new COMException("房台控制失败或响应超时。", unchecked((int)0x80040210)); }
    }
    private async Task<object?[]> InvokeAsync(string name, object?[] inputs)
    {
        _stop.Token.ThrowIfCancellationRequested();
        var action = Schema.Actions.Single(a => a.Name == name);
        var args = action.Arguments.Where(a => a.IsInput).ToArray();
        if (args.Length != inputs.Length) throw new InvalidDataException("控制参数数量无效。");
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var payload = new XElement(XName.Get(name, Service.ServiceType), args.Select((arg, i) =>
            new XElement(arg.Name, arg.Variable.Format(inputs[i]))));
        var envelope = new XDocument(new XElement(soap + "Envelope", new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(soap + "Body", payload)));
        using var request = new HttpRequestMessage(HttpMethod.Post, Service.ControlUrl)
        { Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + Service.ServiceType + "#" + name + "\"");
        using var response = await _http.SendAsync(request, _stop.Token).ConfigureAwait(false);
        var document = ServiceSchema.ReadXml(await response.Content.ReadAsStringAsync(_stop.Token).ConfigureAwait(false));
        var body = document.Root?.Element(soap + "Body") ?? throw new InvalidDataException("SOAP Body 缺失。");
        var fault = body.Element(soap + "Fault");
        if (fault is not null)
        {
            // Never include the response/arguments/Token in COM errors or ordinary logs.
            var code = fault.Descendants().FirstOrDefault(x => x.Name.LocalName == "errorCode")?.Value;
            throw new COMException(int.TryParse(code, out var parsed) ? "房台返回 UPnP 错误 " + parsed + "。" : "房台返回 SOAP Fault。",
                unchecked((int)0x80040210));
        }
        response.EnsureSuccessStatusCode();
        var result = body.Element(XName.Get(name + "Response", Service.ServiceType)) ?? throw new InvalidDataException("SOAP 动作响应不匹配。");
        return action.Arguments.Where(a => !a.IsInput).Select(arg => (object?)arg.Variable.Parse(
            result.Elements().SingleOrDefault(x => x.Name.LocalName == arg.Name)?.Value ?? throw new InvalidDataException("SOAP 输出参数缺失。"))).ToArray();
    }

    public async Task SubscribeAsync(Uri callback)
    {
        if (!Schema.Variables.Any(v => v.Evented)) return;
        if (Service.EventSubUrl is null) throw new InvalidDataException("房台服务缺少事件订阅地址。");
        using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), Service.EventSubUrl);
        request.Headers.TryAddWithoutValidation("CALLBACK", "<" + callback.AbsoluteUri + ">");
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        request.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
        using var response = await _http.SendAsync(request, _stop.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var sid = Header(response, "SID");
        if (!sid.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) || sid.Length is < 6 or > 200 || sid.Any(char.IsControl))
            throw new InvalidDataException("房台返回的事件订阅标识无效。");
        var seconds = Lifetime(response);
        lock (_sync)
        {
            _sid = sid;
            if (_pending is { } pending && pending.Sid == sid) Apply(0, pending.Values);
            _pending = null;
        }
        await _initial.Task.WaitAsync(TimeSpan.FromSeconds(6), _stop.Token).ConfigureAwait(false);
        _renewal = RenewAsync(seconds);
    }
    public bool AcceptEvent(IReadOnlyDictionary<string, string> headers, string xml)
    {
        if (!headers.TryGetValue("SID", out var sid) || sid.Length > 200 || !uint.TryParse(headers.GetValueOrDefault("SEQ"), out var sequence)) return false;
        var document = ServiceSchema.ReadXml(xml);
        if (document.Root?.Name != XName.Get("propertyset", "urn:schemas-upnp-org:event-1-0")) throw new InvalidDataException("GENA 消息无效。");
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var element in document.Root.Elements().Where(x => x.Name.LocalName == "property").SelectMany(x => x.Elements()))
        {
            var variable = Schema.Variables.SingleOrDefault(v => v.Name == element.Name.LocalName);
            if (variable is not null && !values.TryAdd(variable.Name, variable.Parse(element.Value))) throw new InvalidDataException("GENA 状态变量重名。");
        }
        int[] changed;
        lock (_sync)
        {
            if (_disposed) return false;
            if (_sid is null)
            {
                if (sequence == 0) _pending = (sid, values);
                return true;
            }
            if (_sid != sid) return false;
            changed = Apply(sequence, values);
        }
        if (changed.Length > 0) Changed?.Invoke(changed);
        return true;
    }
    private int[] Apply(uint sequence, IReadOnlyDictionary<string, object> values)
    {
        if (_lastSequence is { } last && (sequence == last || (sequence < last && !(last > 0xffff0000 && sequence < 0xffff)))) return [];
        _lastSequence = sequence;
        foreach (var pair in values) { _values[pair.Key] = pair.Value; _received.Add(pair.Key); }
        if (Schema.Variables.Where(v => v.Evented).All(v => _received.Contains(v.Name))) _initial.TrySetResult();
        return values.Keys.Select(name => Schema.Variables.Single(v => v.Name == name).DispId).ToArray();
    }
    private async Task RenewAsync(int? seconds)
    {
        try
        {
            while (true)
            {
                await Task.Delay(seconds is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Max(1, seconds.Value * 0.7)), _stop.Token).ConfigureAwait(false);
                string? sid; lock (_sync) sid = _sid;
                using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), Service.EventSubUrl);
                request.Headers.TryAddWithoutValidation("SID", sid); request.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
                using var response = await _http.SendAsync(request, _stop.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (Header(response, "SID") != sid) throw new InvalidDataException("房台事件订阅身份变化。");
                seconds = Lifetime(response);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { if (!_stop.IsCancellationRequested) _failed(new IOException("房台事件订阅失效，请重新连接。")); }
    }
    private static string Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values)
        ? values.Single() : throw new InvalidDataException("事件响应缺少 " + name + "。");
    private static int? Lifetime(HttpResponseMessage response)
    {
        var value = Header(response, "TIMEOUT");
        if (value.Equals("Second-infinite", StringComparison.OrdinalIgnoreCase)) return null;
        if (!value.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) || !int.TryParse(value.AsSpan(7), out var seconds) || seconds is < 1 or > 86400)
            throw new InvalidDataException("事件订阅时长无效。");
        return seconds;
    }
    public async ValueTask DisposeAsync()
    {
        string? sid;
        lock (_sync) { if (_disposed) return; _disposed = true; sid = _sid; _sid = null; Changed = null; }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_renewal is not null) await _renewal.ConfigureAwait(false);
        if (sid is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), Service.EventSubUrl);
            request.Headers.TryAddWithoutValidation("SID", sid);
            try { using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
