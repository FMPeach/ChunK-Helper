using System.Xml;
using System.Xml.Linq;
using PureKBridge.Core.Models;

namespace PureKBridge.Core.Network;

public sealed record RendererProbeResult(bool IsReachable, bool IsMediaRenderer,
    string? Udn = null, string? FriendlyName = null, string? Error = null,
    RendererDescription? Description = null);

public interface IRendererClient
{
    Task<RendererProbeResult> ProbeAsync(PureKRoomInfo room, CancellationToken cancellationToken);
}

public sealed class RendererClient : IRendererClient, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1024 * 1024 };

    public async Task<RendererProbeResult> ProbeAsync(PureKRoomInfo room, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(room.DescriptionUrl, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return new(false, false, Error: $"设备返回 HTTP {(int)response.StatusCode}");
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var description = ParseDescription(xml, room.DescriptionUrl);
                return new(true, description is not null, description?.Udn, description?.FriendlyName,
                    description is null ? "描述中没有合法的 MediaRenderer。" : null, description);
            }
            catch (XmlException) { return new(true, false, Error: "设备已响应，描述文件尚未确认。"); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, false, Error: "设备响应超时"); }
        catch (HttpRequestException) { return new(false, false, Error: "Renderer 暂时无法访问"); }
    }

    public static RendererDescription? ParseDescription(string xml, Uri descriptionUrl)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = XDocument.Load(reader);
        static string Field(XElement element, string name) =>
            element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim() ?? "";
        var device = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "device" &&
            Field(node, "deviceType").StartsWith("urn:schemas-upnp-org:device:MediaRenderer:", StringComparison.Ordinal));
        if (device is null || !RendererDescription.IsValidUdn(Field(device, "UDN"))) return null;
        var urlBase = descriptionUrl;
        var declaredBase = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        if (!string.IsNullOrEmpty(declaredBase) && Uri.TryCreate(declaredBase, UriKind.Absolute, out var candidate) &&
            IsSameOrigin(candidate, descriptionUrl)) urlBase = candidate;
        Uri? ServiceUrl(string value) => !string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(urlBase, value, out var result) && IsSameOrigin(result, descriptionUrl) ? result : null;
        var services = device.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList")?.Elements()
            .Where(e => e.Name.LocalName == "service")
            .Select(e => new RendererService(Field(e, "serviceType"), Field(e, "serviceId"),
                ServiceUrl(Field(e, "controlURL")), ServiceUrl(Field(e, "eventSubURL")), ServiceUrl(Field(e, "SCPDURL"))))
            .Where(service => service.ServiceType.StartsWith("urn:", StringComparison.Ordinal) &&
                service.ServiceType.All(c => c is >= '!' and <= '~'))
            .DistinctBy(service => service.ServiceType).ToArray() ?? [];
        return new(Field(device, "UDN"), Field(device, "friendlyName"), Field(device, "deviceType"),
            descriptionUrl, services, device.Parent == document.Root);
    }

    public static bool IsSameOrigin(Uri first, Uri second) => first.Scheme is "http" or "https" &&
        first.UserInfo.Length == 0 && first.Scheme == second.Scheme && first.Port == second.Port &&
        first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
