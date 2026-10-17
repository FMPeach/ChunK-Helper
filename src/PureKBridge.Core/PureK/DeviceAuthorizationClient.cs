using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PureKBridge.Core.Models;
using PureKBridge.Core.Network;

namespace PureKBridge.Core.PureK;

public sealed class DeviceConnectionException(string message, Exception? inner = null) : Exception(message, inner);

public interface IDeviceAuthorizationClient
{
    Task SetDeviceAuthorizationAsync(KnownRenderer renderer, bool enable, CancellationToken cancellationToken);
}

public sealed class DeviceAuthorizationClient : IDeviceAuthorizationClient, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1024 * 1024 };

    public async Task SetDeviceAuthorizationAsync(KnownRenderer renderer, bool enable, CancellationToken cancellationToken)
    {
        var service = renderer.Description.ConnectionManager;
        var control = service?.ControlUrl;
        if (service is null || control is null || !RendererClient.IsSameOrigin(control, renderer.Room.RendererBaseUrl))
            throw new DeviceConnectionException("房台没有提供可用的设备登记接口。");
        if (!IPAddress.TryParse(renderer.LocalAddress, out _))
            throw new DeviceConnectionException("尚未获取访问当前房台的本机地址。");
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace action = service.ServiceType;
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap),
                new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(soap + "Body", new XElement(action + "SetDeviceAuthorization",
                    new XAttribute(XNamespace.Xmlns + "u", action),
                    new XElement("DeviceIP", renderer.LocalAddress),
                    new XElement("Token", renderer.Room.Token), new XElement("Enable", enable ? "1" : "0")))));
        using var request = new HttpRequestMessage(HttpMethod.Post, control)
        { Content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#SetDeviceAuthorization\"");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // Error text and the raw response may echo the token. Only report the numeric HTTP result.
            if (response.StatusCode != HttpStatusCode.OK)
                throw new DeviceConnectionException($"房台设备登记失败（HTTP {(int)response.StatusCode}），请确认官方授权及网络连接。");
            try
            {
                using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
                var result = XDocument.Load(reader);
                var body = result.Root?.Element(soap + "Body");
                if (body?.Element(soap + "Fault") is not null)
                    throw new DeviceConnectionException("房台拒绝了设备登记，请确认官方授权后重试。");
                if (body?.Element(action + "SetDeviceAuthorizationResponse") is null)
                    throw new DeviceConnectionException("房台返回的设备登记结果无法确认。");
            }
            catch (XmlException) { throw new DeviceConnectionException("房台返回的设备登记结果不是有效的协议响应。"); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DeviceConnectionException("房台设备登记超时，请检查网络后重试。"); }
        catch (HttpRequestException error)
        { throw new DeviceConnectionException("无法连接房台的设备登记接口，请检查网络。", error); }
    }

    public void Dispose() => _http.Dispose();
}
