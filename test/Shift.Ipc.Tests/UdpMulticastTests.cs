using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Shift.Ipc.Tests;

public sealed class UdpMulticastTests
{
    [Fact]
    public async Task RejectsOversizedSendAndTruncatedReceive()
    {
        var groupAddress = IPAddress.Parse("239.255.42.100");
        int port = GetUnusedPort();

        using UdpMulticastReceiver receiver = new(groupAddress, port, IPAddress.Loopback);
        using UdpMulticastSender sender = new(groupAddress, port, IPAddress.Loopback);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await sender.SendAsync(
                new byte[UnixDatagramReceiver.MaximumDatagramSize + 1],
                timeout.Token));

        Task<int> receiveTask = receiver.ReceiveAsync(new byte[1], timeout.Token).AsTask();
        await sender.SendAsync(new byte[] { 1, 2 }, timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await receiveTask);
    }

    private static int GetUnusedPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
