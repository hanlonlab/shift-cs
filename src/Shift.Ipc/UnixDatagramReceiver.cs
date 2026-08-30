using System.Net.Sockets;

namespace Shift.Ipc;

public sealed class UnixDatagramReceiver : IDisposable
{
    public const int MaximumDatagramSize = 2_048;

    private readonly Socket _socket;
    private readonly UnixDomainSocketEndPoint _endPoint;

    public UnixDatagramReceiver(string path)
    {
        _endPoint = new UnixDomainSocketEndPoint(path);
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);

        try
        {
            _socket.Bind(_endPoint);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
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
        Memory<byte> receiveBuffer = destination[..Math.Min(destination.Length, MaximumDatagramSize)];
        SocketReceiveMessageFromResult result = await _socket.ReceiveMessageFromAsync(
            receiveBuffer,
            SocketFlags.None,
            _endPoint,
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
