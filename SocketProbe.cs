using System.Net;
using System.Net.Sockets;

namespace NetScopePLC;

internal static class SocketProbe
{
    public static async Task<Socket> ConnectAsync(string remote, int port, IPAddress? bindAddress, TimeSpan timeout)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (bindAddress is not null)
            socket.Bind(new IPEndPoint(bindAddress, 0));
        using var cts = new CancellationTokenSource(timeout);
        await socket.ConnectAsync(IPAddress.Parse(remote), port, cts.Token);
        return socket;
    }

    public static async Task<bool> TryConnectAsync(string remote, int port, IPAddress? bindAddress, TimeSpan timeout)
    {
        try
        {
            using var socket = await ConnectAsync(remote, port, bindAddress, timeout);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
