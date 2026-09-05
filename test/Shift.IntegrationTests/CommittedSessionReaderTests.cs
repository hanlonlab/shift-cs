using System.Net;
using System.Net.Sockets;
using Shift.Engine.EngineHost;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Control;
using Xunit;

namespace Shift.IntegrationTests;

public sealed class CommittedSessionReaderTests
{
    [Fact]
    public async Task WaitsForWatermarkAndPreservesFramesAcrossCancellationDuplicatesAndOtherSessions()
    {
        using MulticastFixture fixture = new();
        CanonicalFrame start = fixture.Candidate(MessageType.StartNewSession, 1);
        CanonicalFrame quote = fixture.Candidate(MessageType.UpdateReferenceQuote, 2, [0x12, 0x34]);
        var staleSession = Guid.NewGuid();
        await fixture.SendAsync(FrameCodec.Encode(MessageType.StartNewSession, staleSession, 1, 1, 1, []));
        await fixture.SendAsync(CommitThroughCodec.Encode(staleSession, 1));
        await fixture.SendAsync(start);
        await fixture.SendAsync(start);
        await fixture.SendAsync(quote);

        using (var silence = CancellationTokenSource.CreateLinkedTokenSource(fixture.Timeout.Token))
        {
            silence.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fixture.Reader.ReadBatchAsync(silence.Token));
        }

        Assert.Equal(0, fixture.Reader.LastCommittedSequence);
        await fixture.SendAsync(CommitThroughCodec.Encode(fixture.SessionId, 2));
        IReadOnlyList<CanonicalFrame> first = await fixture.Reader.ReadBatchAsync(fixture.Timeout.Token);
        Assert.Equal(2, fixture.Reader.LastCommittedSequence);
        Assert.Equal(2, first.Count);
        Assert.Equal(start.Bytes.ToArray(), first[0].Bytes.ToArray());
        Assert.Equal(quote.Bytes.ToArray(), first[1].Bytes.ToArray());

        await fixture.SendAsync(start);
        await fixture.SendAsync(quote);
        await fixture.SendAsync(CommitThroughCodec.Encode(fixture.SessionId, 2));
        CanonicalFrame end = fixture.Candidate(MessageType.EndCurrentSession, 3);
        await fixture.SendAsync(end);
        await fixture.SendAsync(CommitThroughCodec.Encode(fixture.SessionId, 2));
        await fixture.SendAsync(CommitThroughCodec.Encode(fixture.SessionId, 3));
        IReadOnlyList<CanonicalFrame> second = await fixture.Reader.ReadBatchAsync(fixture.Timeout.Token);
        Assert.Equal(end.Bytes.ToArray(), Assert.Single(second).Bytes.ToArray());
        Assert.Equal(3, fixture.Reader.LastCommittedSequence);
        Assert.Equal(start.Bytes.ToArray(), first[0].Bytes.ToArray());
        Assert.Equal(quote.Bytes.ToArray(), first[1].Bytes.ToArray());
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("ahead watermark")]
    [InlineData("partial watermark")]
    [InlineData("conflicting duplicate")]
    [InlineData("submission")]
    [InlineData("control producer")]
    [InlineData("bad checksum")]
    [InlineData("malformed watermark")]
    public async Task InvalidStreamFailsWithoutCommittingAndCannotResume(string failure)
    {
        using MulticastFixture fixture = new();
        await fixture.SendAsync(fixture.Candidate(MessageType.StartNewSession, 1));
        switch (failure)
        {
            case "gap":
                await fixture.SendAsync(fixture.Candidate(MessageType.PlaceOrder, 3));
                break;
            case "ahead watermark":
                await fixture.SendAsync(CommitThroughCodec.Encode(fixture.SessionId, 2));
                break;
            case "partial watermark":
                await fixture.SendAsync(fixture.Candidate(MessageType.PlaceOrder, 2));
                await fixture.SendAsync(CommitThroughCodec.Encode(fixture.SessionId, 1));
                break;
            case "conflicting duplicate":
                await fixture.SendAsync(fixture.Candidate(MessageType.PlaceOrder, 1));
                break;
            case "submission":
                await fixture.SendAsync(fixture.Candidate(MessageType.PlaceOrder, 0));
                break;
            case "control producer":
                await fixture.SendAsync(FrameCodec.Encode(MessageType.PlaceOrder, fixture.SessionId, 0, 2, 2, []));
                break;
            case "bad checksum":
                byte[] corrupt = fixture.Candidate(MessageType.PlaceOrder, 2).Bytes.ToArray();
                corrupt[^1] ^= 0xff;
                await fixture.Sender.SendAsync(corrupt, fixture.Timeout.Token);
                break;
            case "malformed watermark":
                await fixture.SendAsync(FrameCodec.Encode(MessageType.CommitThrough, fixture.SessionId, 0, 0, 1, [1]));
                break;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Reader.ReadBatchAsync(fixture.Timeout.Token));
        Assert.Equal(0, fixture.Reader.LastCommittedSequence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Reader.ReadBatchAsync(fixture.Timeout.Token));
    }

    [Theory]
    [InlineData(MessageType.PlaceOrder, false)]
    [InlineData(MessageType.StartNewSession, true)]
    public async Task FirstFrameMustBeAValidSessionStart(MessageType messageType, bool invalidPayload)
    {
        using MulticastFixture fixture = new();
        await fixture.SendAsync(fixture.Candidate(messageType, 1, invalidPayload ? [1] : []));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Reader.ReadBatchAsync(fixture.Timeout.Token));
    }

    private sealed class MulticastFixture : IDisposable
    {
        private readonly UdpMulticastReceiver _receiver;

        public MulticastFixture()
        {
            var group = IPAddress.Parse("239.255.43.20");
            int port;
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }

            _receiver = new UdpMulticastReceiver(group, port, IPAddress.Loopback);
            Sender = new UdpMulticastSender(group, port, IPAddress.Loopback);
            Reader = new CommittedSessionReader(_receiver, SessionId);
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(10));

        public UdpMulticastSender Sender { get; }

        public CommittedSessionReader Reader { get; }

        public CanonicalFrame Candidate(MessageType messageType, long sequence, ReadOnlySpan<byte> payload = default)
        {
            return FrameCodec.Encode(messageType, SessionId, 1, (ulong)Math.Max(sequence, 1), sequence, payload);
        }

        public async Task SendAsync(CanonicalFrame frame)
        {
            await Sender.SendAsync(frame.Bytes, Timeout.Token);
        }

        public void Dispose()
        {
            _receiver.Dispose();
            Sender.Dispose();
            Timeout.Dispose();
        }
    }
}
