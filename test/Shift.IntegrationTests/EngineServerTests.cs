using System.Net;
using System.Net.Sockets;
using Shift.Engine.EngineHost;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.Protocol.Internal.Control;
using Shift.Protocol.Internal.Events;
using Xunit;

namespace Shift.IntegrationTests;

public sealed class EngineServerTests
{
    [Fact]
    public async Task PartialIocWaitsForCommitAndObservesItsResultsBeforeEnding()
    {
        await using EngineFixture fixture = new();
        await fixture.SendInputsAsync(OrderType.ImmediateOrCancelLimit);
        await fixture.AssertNoSubmissionAsync();
        Assert.False(fixture.Engine.IsSessionActive);
        Assert.Equal(0, fixture.Engine.AppliedThrough);

        await fixture.CommitAsync();
        CanonicalFrame updateFrame = await fixture.ReceiveSubmissionAsync();
        CanonicalFrame executionFrame = await fixture.ReceiveSubmissionAsync();

        AssertProposal(updateFrame, fixture.SessionId, MessageType.OrderUpdated, 1);
        Assert.True(OrderUpdatedCodec.TryDecode(updateFrame.Payload.Span, out OrderUpdated update));
        Assert.Equal(new OrderUpdated(1, 7, 0, 5, RejectionReason.None, CancellationReason.ImmediateOrCancel), update);
        AssertProposal(executionFrame, fixture.SessionId, MessageType.TradeExecuted, 2);
        Assert.True(TradeExecutedCodec.TryDecode(executionFrame.Payload.Span, out TradeExecuted execution));
        Assert.Equal(new TradeExecuted(1, new Fill(7, 101, 10, FillRole.Taker)), execution);

        await fixture.EchoAsync(updateFrame);
        await fixture.EchoAsync(executionFrame);
        await fixture.SendCommandAsync(MessageType.EndCurrentSession);
        await fixture.CommitAsync();
        await fixture.RunTask.WaitAsync(fixture.Timeout.Token);

        Assert.Equal(6, fixture.Engine.AppliedThrough);
        Assert.Equal(2, fixture.Engine.ObservedResultCount);
        Assert.False(fixture.Engine.IsSessionActive);
        await fixture.AssertNoSubmissionAsync();
    }

    [Fact]
    public async Task UnsupportedDayOrderIsRejectedWithoutLeavingARestingOrder()
    {
        await using EngineFixture fixture = new();
        await fixture.SendInputsAsync(OrderType.DayLimit);
        await fixture.CommitAsync();
        CanonicalFrame updateFrame = await fixture.ReceiveSubmissionAsync();

        AssertProposal(updateFrame, fixture.SessionId, MessageType.OrderUpdated, 1);
        Assert.True(OrderUpdatedCodec.TryDecode(updateFrame.Payload.Span, out OrderUpdated update));
        Assert.Equal(new OrderUpdated(1, 7, 0, 0, RejectionReason.UnsupportedOrderType, CancellationReason.None), update);
        await fixture.AssertNoSubmissionAsync();

        await fixture.EchoAsync(updateFrame);
        await fixture.SendCommandAsync(MessageType.EndCurrentSession);
        await fixture.CommitAsync();
        await fixture.RunTask.WaitAsync(fixture.Timeout.Token);

        // EndSession has no cancellation buffer, so a resting order would prevent completion.
        Assert.Equal(5, fixture.Engine.AppliedThrough);
        Assert.Equal(1, fixture.Engine.ObservedResultCount);
        Assert.False(fixture.Engine.IsSessionActive);
        await fixture.AssertNoSubmissionAsync();
    }

    [Fact]
    public async Task SessionEndFailsWhileGeneratedResultsRemainUncommitted()
    {
        await using EngineFixture fixture = new();
        await fixture.SendInputsAsync(OrderType.ImmediateOrCancelLimit);
        await fixture.CommitAsync();
        await fixture.ReceiveSubmissionAsync();
        await fixture.ReceiveSubmissionAsync();

        await fixture.SendCommandAsync(MessageType.EndCurrentSession);
        await fixture.CommitAsync();

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.RunTask.WaitAsync(fixture.Timeout.Token));
        Assert.Contains("results must be observed committed", failure.Message);
        Assert.Equal(3, fixture.Engine.AppliedThrough);
        Assert.Equal(0, fixture.Engine.ObservedResultCount);
        Assert.True(fixture.Engine.IsSessionActive);
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("producer sequence")]
    [InlineData("type")]
    [InlineData("payload")]
    public async Task ConflictingCommittedResultStopsTheHost(string conflict)
    {
        await using EngineFixture fixture = new();
        await fixture.SendInputsAsync(OrderType.ImmediateOrCancelLimit);
        await fixture.CommitAsync();
        CanonicalFrame update = await fixture.ReceiveSubmissionAsync();
        await fixture.ReceiveSubmissionAsync();

        byte[] payload = update.Payload.ToArray();
        if (conflict == "payload")
        {
            payload[^1] ^= 1;
        }

        CanonicalFrame conflicting = FrameCodec.Encode(
            conflict == "type" ? MessageType.TradeExecuted : update.Header.MessageType,
            fixture.SessionId,
            conflict == "producer" ? (ushort)3 : update.Header.ProducerId,
            conflict == "producer sequence" ? 2UL : update.Header.ProducerSequence,
            0,
            payload);
        await fixture.EchoAsync(conflicting);
        await fixture.CommitAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunTask.WaitAsync(fixture.Timeout.Token));
        Assert.Equal(3, fixture.Engine.AppliedThrough);
        Assert.Equal(0, fixture.Engine.ObservedResultCount);
    }

    private static void AssertProposal(CanonicalFrame frame, Guid sessionId, MessageType messageType, ulong producerSequence)
    {
        Assert.Equal(sessionId, frame.Header.SessionId);
        Assert.Equal(messageType, frame.Header.MessageType);
        Assert.Equal((ushort)2, frame.Header.ProducerId);
        Assert.Equal(producerSequence, frame.Header.ProducerSequence);
        Assert.Equal(0, frame.Header.SequenceId);
    }

    private sealed class EngineFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        private readonly UdpMulticastReceiver _committed;
        private readonly UdpMulticastSender _multicast;
        private readonly UnixDatagramReceiver _submissionReceiver;
        private readonly UnixDatagramSender _submissions;
        private long _sequence;
        private ulong _participantSequence;

        public EngineFixture()
        {
            Assert.SkipWhen(
                !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
                "AF_UNIX integration test requires macOS or Linux.");

            Directory.CreateDirectory(_directory);
            string submissionPath = Path.Combine(_directory, "in.sock");
            var group = IPAddress.Parse("239.255.43.21");
            int port;
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }

            _committed = new UdpMulticastReceiver(group, port, IPAddress.Loopback);
            _multicast = new UdpMulticastSender(group, port, IPAddress.Loopback);
            _submissionReceiver = new UnixDatagramReceiver(submissionPath);
            _submissions = new UnixDatagramSender(submissionPath);
            Engine = new EngineServer(new CommittedSessionReader(_committed, SessionId), _submissions, 1, 2);
            RunTask = Engine.RunAsync(Timeout.Token);
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(10));

        public EngineServer Engine { get; }

        public Task RunTask { get; }

        public async Task SendInputsAsync(OrderType orderType)
        {
            await SendCommandAsync(MessageType.StartNewSession);
            byte[] quote = new byte[40];
            UpdateReferenceQuoteCodec.Encode(
                new UpdateReferenceQuote(1, new ReferenceLevel(99, 10), new ReferenceLevel(101, 10)), quote);
            await SendCommandAsync(MessageType.UpdateReferenceQuote, quote);
            byte[] order = new byte[34];
            PlaceOrderCodec.Encode(new PlaceOrder(1, 7, OrderSide.Buy, 101, 15, orderType), order);
            await SendCommandAsync(MessageType.PlaceOrder, order);
        }

        public async Task SendCommandAsync(MessageType messageType, ReadOnlyMemory<byte> payload = default)
        {
            CanonicalFrame frame = FrameCodec.Encode(
                messageType, SessionId, 1, ++_participantSequence, ++_sequence, payload.Span);
            await _multicast.SendAsync(frame.Bytes, Timeout.Token);
        }

        public async Task EchoAsync(CanonicalFrame proposal)
        {
            CanonicalFrame frame = FrameCodec.Encode(
                proposal.Header.MessageType, SessionId, proposal.Header.ProducerId,
                proposal.Header.ProducerSequence, ++_sequence, proposal.Payload.Span);
            await _multicast.SendAsync(frame.Bytes, Timeout.Token);
        }

        public async Task CommitAsync()
        {
            await _multicast.SendAsync(CommitThroughCodec.Encode(SessionId, _sequence).Bytes, Timeout.Token);
        }

        public async Task<CanonicalFrame> ReceiveSubmissionAsync()
        {
            byte[] buffer = new byte[FrameCodec.MaximumFrameSize];
            int length = await _submissionReceiver.ReceiveAsync(buffer, Timeout.Token);
            return FrameCodec.DecodeSubmission(buffer.AsMemory(0, length));
        }

        public async Task AssertNoSubmissionAsync()
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(Timeout.Token);
            silence.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await _submissionReceiver.ReceiveAsync(new byte[FrameCodec.MaximumFrameSize], silence.Token));
        }

        public async ValueTask DisposeAsync()
        {
            await Timeout.CancelAsync();
            if (!RunTask.IsCompleted)
            {
                try
                {
                    await RunTask;
                }
                catch (OperationCanceledException) when (Timeout.IsCancellationRequested)
                {
                }
            }

            _submissions.Dispose();
            _submissionReceiver.Dispose();
            _multicast.Dispose();
            _committed.Dispose();
            Timeout.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
