using System.Net;
using System.Net.Sockets;

namespace Shift.Ipc;

public sealed class UdpMulticastReceiver : IDisposable
{
    private readonly Socket _socket;

    public UdpMulticastReceiver(
        IPAddress groupAddress,
        int port,
        IPAddress localInterface)
    {
        if (groupAddress.AddressFamily != AddressFamily.InterNetwork
            || groupAddress.GetAddressBytes()[0] is < 224 or > 239)
        {
            throw new ArgumentException("The group address must be an IPv4 multicast address.", nameof(groupAddress));
        }

        if (localInterface.AddressFamily != AddressFamily.InterNetwork
            || localInterface.Equals(IPAddress.Any))
        {
            throw new ArgumentException("The local interface must be a specific IPv4 address.", nameof(localInterface));
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(groupAddress, localInterface));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        Memory<byte> receiveBuffer = destination[..Math.Min(
            destination.Length,
            UnixDatagramReceiver.MaximumDatagramSize)];
        SocketReceiveMessageFromResult result = await _socket.ReceiveMessageFromAsync(
            receiveBuffer,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Any, 0),
            cancellationToken);

        if ((result.SocketFlags & SocketFlags.Truncated) != 0)
        {
            throw new InvalidDataException("The received datagram exceeded the destination buffer.");
        }

        return result.ReceivedBytes;
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
