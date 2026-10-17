namespace PureKBridge.Core.Models;

public sealed class PureKRoomInfo
{
    public required string RoomName { get; init; }
    public required string PlaceCode { get; init; }
    public string? BillId { get; init; }
    public required Uri OriginalQrUrl { get; init; }
    public required Uri ProjectionUrl { get; init; }
    public required Uri RendererBaseUrl { get; init; }
    public required string Token { get; init; }
    public string? AppId { get; init; }

    public Uri DescriptionUrl
    {
        get
        {
            var builder = new UriBuilder(RendererBaseUrl)
            {
                Path = RendererBaseUrl.AbsolutePath.TrimEnd('/') + "/description.xml",
                Query = "", Fragment = ""
            };
            return builder.Uri;
        }
    }

    public string MaskedToken => TokenMask.Mask(Token);
    public override string ToString() => $"{RoomName} ({RendererBaseUrl.Authority}), Token={MaskedToken}";
}

public static class TokenMask
{
    public static string Mask(string? token) => string.IsNullOrEmpty(token)
        ? "未提供"
        : (token.Length > 8 ? token[..6] : "") + "••••••";
}
