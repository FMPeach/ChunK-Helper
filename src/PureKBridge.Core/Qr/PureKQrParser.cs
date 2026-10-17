using System.Text;
using PureKBridge.Core.Models;

namespace PureKBridge.Core.Qr;

public sealed class PureKQrParser : IPureKQrParser
{
    public PureKRoomInfo Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32768 ||
            !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var outer) ||
            !IsWebUrl(outer) || !outer.Host.Equals("wx.chunk.cn", StringComparison.OrdinalIgnoreCase) ||
            !outer.AbsolutePath.TrimEnd('/').Equals("/projection/login", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("二维码不是支持的 Pure K 投屏二维码。");

        try
        {
            var outerQuery = Query(outer);
            var encoded = Required(outerQuery, "projection_url").Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var innerText = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
            if (!Uri.TryCreate(innerText, UriKind.Absolute, out var projection))
                Uri.TryCreate(Uri.UnescapeDataString(innerText), UriKind.Absolute, out projection);
            if (projection is null || !IsWebUrl(projection) ||
                !projection.Host.Equals("conn.dolphinstar.cn", StringComparison.OrdinalIgnoreCase) ||
                !projection.AbsolutePath.TrimEnd('/').Equals("/Remote", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("投屏链接中的设备入口不受支持。");

            var inner = Query(projection);
            if (!Uri.TryCreate(Required(inner, "url"), UriKind.Absolute, out var renderer) ||
                !IsWebUrl(renderer) || renderer.UserInfo.Length != 0)
                throw new FormatException("二维码中的设备地址无效。");

            var place = Required(outerQuery, "place_code");
            return new PureKRoomInfo
            {
                RoomName = Optional(inner, "name") is { Length: > 0 } name ? name : $"房间 {place}",
                PlaceCode = place,
                BillId = Optional(outerQuery, "bill_id"),
                OriginalQrUrl = outer,
                ProjectionUrl = projection,
                RendererBaseUrl = renderer,
                Token = Required(inner, "token"),
                AppId = Optional(inner, "appid")
            };
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or ArgumentException)
        {
            // Never put the raw QR URL or an opaque authorization token in errors.
            throw new FormatException("二维码解析失败，请确认使用的是完整的房间投屏二维码。");
        }
    }

    private static bool IsWebUrl(Uri uri) => uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length == 2 ? pair[1] : "";
            // Base64 and opaque tokens may contain literal '+'. Only human-readable
            // name fields use the HTML form convention '+' = space.
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) value = value.Replace('+', ' ');
            if (!result.TryAdd(key, Uri.UnescapeDataString(value)))
                throw new FormatException("二维码包含重复字段。");
        }
        return result;
    }

    private static string Required(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new FormatException("二维码缺少必要字段。");
    private static string? Optional(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
