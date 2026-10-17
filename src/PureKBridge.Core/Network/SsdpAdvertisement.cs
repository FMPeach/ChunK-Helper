using System.Text;
using PureKBridge.Core.Models;

namespace PureKBridge.Core.Network;

public sealed record SsdpIdentity(string NotificationType, string Usn);

public static class SsdpAdvertisement
{
    public const string MulticastHost = "239.255.255.250:1900";

    public static IReadOnlyList<SsdpIdentity> Identities(RendererDescription description)
    {
        if (!RendererDescription.IsValidUdn(description.Udn) ||
            !description.DeviceType.StartsWith("urn:schemas-upnp-org:device:MediaRenderer:", StringComparison.Ordinal))
            throw new ArgumentException("设备描述缺少真实的 MediaRenderer 身份。");
        static void Validate(string value)
        {
            if (value.Length > 512 || value.Any(c => c is < '!' or > '~'))
                throw new ArgumentException("设备描述含有无效的公告字段。");
        }
        var types = new List<string> { description.Udn, description.DeviceType };
        if (description.IsRootDevice) types.Insert(0, "upnp:rootdevice");
        types.AddRange(description.Services.Select(service => service.ServiceType));
        return types.Distinct(StringComparer.OrdinalIgnoreCase).Select(type =>
        {
            Validate(type);
            return new SsdpIdentity(type, type == description.Udn ? description.Udn : description.Udn + "::" + type);
        }).ToArray();
    }

    public static byte[] Alive(RendererDescription description, SsdpIdentity identity)
    {
        var location = description.DescriptionUrl;
        if (location.Scheme is not ("http" or "https") || location.IsLoopback || location.UserInfo.Length > 0)
            throw new ArgumentException("公告必须指向真实、非回环的 Renderer 地址。");
        return Encoding.ASCII.GetBytes("NOTIFY * HTTP/1.1\r\n" +
            $"HOST: {MulticastHost}\r\nCACHE-CONTROL: max-age=60\r\nLOCATION: {location.AbsoluteUri}\r\n" +
            $"NT: {identity.NotificationType}\r\nNTS: ssdp:alive\r\n" +
            "SERVER: Windows/10.0 UPnP/1.0 ChunKHelper/0.1\r\n" +
            $"USN: {identity.Usn}\r\n\r\n");
    }

    public static byte[] ByeBye(SsdpIdentity identity) => Encoding.ASCII.GetBytes(
        $"NOTIFY * HTTP/1.1\r\nHOST: {MulticastHost}\r\nNT: {identity.NotificationType}\r\n" +
        $"NTS: ssdp:byebye\r\nUSN: {identity.Usn}\r\n\r\n");
}
