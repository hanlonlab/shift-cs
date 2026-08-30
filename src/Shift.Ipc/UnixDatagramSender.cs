using System.Net;
using System.Net.Sockets;

namespace Shift.Ipc;

public sealed class UnixDatagramSender : IDisposable
{
    private readonly Socket _socket;
    private readonly EndPoint _destination;

    public UnixDatagramSender(string destinationPath)
    {
        _destination = new UnixDomainSocketEndPoint(destinationPath);
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
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
