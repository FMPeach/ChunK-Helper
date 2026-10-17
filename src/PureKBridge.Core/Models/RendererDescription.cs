namespace PureKBridge.Core.Models;

public sealed record RendererService(string ServiceType, string ServiceId,
    Uri? ControlUrl, Uri? EventSubUrl, Uri? ScpdUrl);

public sealed record RendererDescription(string Udn, string FriendlyName, string DeviceType,
    Uri DescriptionUrl, IReadOnlyList<RendererService> Services, bool IsRootDevice)
{
    public RendererService? ConnectionManager => Services.FirstOrDefault(service =>
        service.ServiceType.StartsWith("urn:schemas-upnp-org:service:ConnectionManager:", StringComparison.Ordinal));

    public static bool IsValidUdn(string? value) => value is { Length: > 5 and < 200 } &&
        value.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(value.AsSpan(5), out _);
}
