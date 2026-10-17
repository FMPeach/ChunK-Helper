using System.Net;
using System.Net.Sockets;

namespace PureKBridge.Core.Network;

public interface ILocalRouteResolver
{
    Task<IPAddress> ResolveAsync(Uri renderer, CancellationToken cancellationToken);
}

public sealed class LocalRouteResolver : ILocalRouteResolver
{
    public async Task<IPAddress> ResolveAsync(Uri renderer, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(renderer.DnsSafeHost, cancellationToken);
        SocketException? failure = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork))
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // UDP connect chooses a route without transmitting a datagram.
                await socket.ConnectAsync(new IPEndPoint(address, renderer.Port), cancellationToken);
                return ((IPEndPoint)socket.LocalEndPoint!).Address;
            }
            catch (SocketException error) { failure = error; }
        }
        throw new IOException("未找到可以到达房台的本机网络地址。", failure);
    }
}
