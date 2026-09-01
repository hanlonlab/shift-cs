using System.Net;
using System.Net.Sockets;

namespace Shift.Ipc;

public sealed class UdpMulticastSender : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _destination;

    public UdpMulticastSender(
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

        _destination = new IPEndPoint(groupAddress, port);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            _socket.Bind(new IPEndPoint(localInterface, 0));
            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                localInterface.GetAddressBytes());
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            _socket.MulticastLoopback = true;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default)
    {
        if (datagram.Length > UnixDatagramReceiver.MaximumDatagramSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(datagram),
                $"A datagram cannot exceed {UnixDatagramReceiver.MaximumDatagramSize} bytes.");
        }

        int bytesSent = await _socket.SendToAsync(
            datagram,
            SocketFlags.None,
            _destination,
            cancellationToken);
        if (bytesSent != datagram.Length)
        {
            throw new IOException($"Sent {bytesSent} of {datagram.Length} bytes.");
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
