using PureKBridge.Core.Models;

namespace PureKBridge.Core.Qr;

public interface IPureKQrParser
{
    PureKRoomInfo Parse(string raw);
}
