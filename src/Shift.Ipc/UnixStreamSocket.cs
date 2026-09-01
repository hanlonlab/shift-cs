using System.Net.Sockets;

namespace Shift.Ipc;

public sealed class UnixStreamSocket : IDisposable
{
    private readonly Socket _socket;

    private UnixStreamSocket(Socket socket)
    {
        _socket = socket;
    }

    public static UnixStreamSocket Listen(string path)
    {
        UnixDomainSocketEndPoint endPoint = new(path);
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            socket.Bind(endPoint);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            socket.Listen();
            return new UnixStreamSocket(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async ValueTask<UnixStreamSocket> ConnectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
            return new UnixStreamSocket(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask<UnixStreamSocket> AcceptAsync(
        CancellationToken cancellationToken = default)
    {
        Socket socket = await _socket.AcceptAsync(cancellationToken);
        return new UnixStreamSocket(socket);
    }

    public async ValueTask SendExactlyAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        int bytesSent = 0;
        while (bytesSent < source.Length)
        {
            int count = await _socket.SendAsync(
                source[bytesSent..],
                SocketFlags.None,
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("The socket closed before all bytes were sent.");
            }

            bytesSent += count;
        }
    }

    public async ValueTask ReceiveExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        int bytesReceived = 0;
        while (bytesReceived < destination.Length)
        {
            int count = await _socket.ReceiveAsync(
                destination[bytesReceived..],
                SocketFlags.None,
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("The socket closed before all bytes were received.");
            }

            bytesReceived += count;
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
