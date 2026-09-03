using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Shift.Archiver;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Control;
using Shift.Sequencer;
using Xunit;

namespace Shift.IntegrationTests;

public sealed class SequencerLivePathTests
{
    private const ushort ProducerId = 1;

    [Fact]
    public async Task QuietBatchCommitsAndNextSessionRestartsAtOneInANewLog()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX integration test requires macOS or Linux.");

        string directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        string archiveRoot = Path.Combine(directory, "archive");
        string submissionPath = Path.Combine(directory, "in.sock");
        string archiverPath = Path.Combine(directory, "archive.sock");
        var group = IPAddress.Parse("239.255.43.1");
        int port = GetUnusedPort();
        Directory.CreateDirectory(archiveRoot);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            using var listener = UnixStreamSocket.Listen(archiverPath);
            using UdpMulticastReceiver committed = new(group, port, IPAddress.Loopback);
            using UnixDatagramReceiver submissionReceiver = new(submissionPath);
            using UnixStreamSocket archiverConnection = await UnixStreamSocket.ConnectAsync(
                archiverPath,
                timeout.Token);
            using UdpMulticastSender multicast = new(group, port, IPAddress.Loopback);

            SequencerServer sequencer = new(
                submissionReceiver,
                archiverConnection,
                multicast);
            Task sequencerTask = sequencer.RunAsync(timeout.Token);
            using UnixStreamSocket archiverStream = await listener.AcceptAsync(timeout.Token);
            using ArchiverServer archiver = new(archiveRoot);
            Task archiverTask = archiver.RunAsync(archiverStream, timeout.Token);
            using UnixDatagramSender submissions = new(submissionPath);

            try
            {
                Guid firstSessionId = new("00112233-4455-6677-8899-aabbccddeeff");
                await submissions.SendAsync(
                    EncodeStart(ProducerId, 1, firstSessionId),
                    timeout.Token);

                FrameHeader firstStart = await ReceiveAsync(committed, timeout.Token);
                FrameHeader firstStartCommit = await ReceiveAsync(committed, timeout.Token);
                Assert.Equal(MessageType.StartNewSession, firstStart.MessageType);
                Assert.Equal(firstSessionId, firstStart.SessionId);
                Assert.Equal(1, firstStart.SequenceId);
                AssertCommit(firstStartCommit, firstSessionId, 1);

                await submissions.SendAsync(
                    EncodeSubmission(MessageType.PlaceOrder, firstSessionId, ProducerId, 2, [0x01]),
                    timeout.Token);
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.EndCurrentSession, firstSessionId, ProducerId, 3, []),
                    timeout.Token);

                FrameHeader order = await ReceiveAsync(committed, timeout.Token);
                FrameHeader firstEnd = await ReceiveAsync(committed, timeout.Token);
                FrameHeader firstEndCommit = await ReceiveAsync(committed, timeout.Token);
                Assert.Equal(MessageType.PlaceOrder, order.MessageType);
                Assert.Equal(firstSessionId, order.SessionId);
                Assert.Equal(2, order.SequenceId);
                Assert.Equal(MessageType.EndCurrentSession, firstEnd.MessageType);
                Assert.Equal(firstSessionId, firstEnd.SessionId);
                Assert.Equal(3, firstEnd.SequenceId);
                AssertCommit(firstEndCommit, firstSessionId, 3);

                Guid secondSessionId = new("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");
                await submissions.SendAsync(
                    EncodeStart(ProducerId, 1, secondSessionId),
                    timeout.Token);

                FrameHeader secondStart = await ReceiveAsync(committed, timeout.Token);
                FrameHeader secondStartCommit = await ReceiveAsync(committed, timeout.Token);
                Assert.Equal(MessageType.StartNewSession, secondStart.MessageType);
                Assert.Equal(secondSessionId, secondStart.SessionId);
                Assert.Equal(1, secondStart.SequenceId);
                AssertCommit(secondStartCommit, secondSessionId, 1);

                await submissions.SendAsync(
                    EncodeSubmission(MessageType.PlaceOrder, firstSessionId, ProducerId, 2, [0x01]),
                    timeout.Token);
                await AssertNoMulticastAsync(committed, timeout.Token);

                await submissions.SendAsync(
                    EncodeSubmission(MessageType.EndCurrentSession, secondSessionId, ProducerId, 2, []),
                    timeout.Token);

                FrameHeader secondEnd = await ReceiveAsync(committed, timeout.Token);
                FrameHeader secondEndCommit = await ReceiveAsync(committed, timeout.Token);
                Assert.Equal(MessageType.EndCurrentSession, secondEnd.MessageType);
                Assert.Equal(secondSessionId, secondEnd.SessionId);
                Assert.Equal(2, secondEnd.SequenceId);
                AssertCommit(secondEndCommit, secondSessionId, 2);

                Assert.True(File.Exists(Path.Combine(archiveRoot, $"{firstSessionId:N}.shiftlog")));
                Assert.True(File.Exists(Path.Combine(archiveRoot, $"{secondSessionId:N}.shiftlog")));
            }
            finally
            {
                await CancelAsync(timeout, sequencerTask, archiverTask);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(InvalidAcknowledgement.Length)]
    [InlineData(InvalidAcknowledgement.Checksum)]
    [InlineData(InvalidAcknowledgement.MessageType)]
    [InlineData(InvalidAcknowledgement.ProducerId)]
    [InlineData(InvalidAcknowledgement.ProducerSequence)]
    [InlineData(InvalidAcknowledgement.Payload)]
    [InlineData(InvalidAcknowledgement.HighWater)]
    [InlineData(InvalidAcknowledgement.SessionId)]
    public async Task DoesNotMulticastBeforeAnExactDurableAcknowledgement(
        InvalidAcknowledgement invalidAcknowledgement)
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX integration test requires macOS or Linux.");

        string directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        string submissionPath = Path.Combine(directory, "in.sock");
        string archiverPath = Path.Combine(directory, "archive.sock");
        var group = IPAddress.Parse("239.255.43.2");
        int port = GetUnusedPort();
        Directory.CreateDirectory(directory);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            using var listener = UnixStreamSocket.Listen(archiverPath);
            using UdpMulticastReceiver committed = new(group, port, IPAddress.Loopback);
            using UnixDatagramReceiver submissionReceiver = new(submissionPath);
            using UnixStreamSocket archiverConnection = await UnixStreamSocket.ConnectAsync(
                archiverPath,
                timeout.Token);
            using UdpMulticastSender multicast = new(group, port, IPAddress.Loopback);
            SequencerServer sequencer = new(
                submissionReceiver,
                archiverConnection,
                multicast);
            Task sequencerTask = sequencer.RunAsync(timeout.Token);
            using UnixStreamSocket stream = await listener.AcceptAsync(timeout.Token);
            using UnixDatagramSender submissions = new(submissionPath);

            try
            {
                Guid sessionId = new("60718293-a4b5-c6d7-e8f9-0a1b2c3d4e5f");
                await submissions.SendAsync(
                    EncodeStart(
                        ProducerId,
                        1,
                        sessionId),
                    timeout.Token);

                FrameHeader candidate = await ReceiveCandidateAsync(stream, timeout.Token);
                Assert.Equal(MessageType.StartNewSession, candidate.MessageType);
                Assert.Equal(sessionId, candidate.SessionId);
                Assert.Equal(1, candidate.SequenceId);
                await AssertNoMulticastAsync(committed, timeout.Token);

                await stream.SendExactlyAsync(
                    EncodeInvalidAcknowledgement(invalidAcknowledgement, sessionId),
                    timeout.Token);

                await Assert.ThrowsAsync<InvalidDataException>(async () => await sequencerTask);
                await AssertNoMulticastAsync(committed, timeout.Token);
            }
            finally
            {
                if (!sequencerTask.IsCompleted)
                {
                    await CancelAsync(timeout, sequencerTask);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<FrameHeader> ReceiveAsync(
        UdpMulticastReceiver receiver,
        CancellationToken cancellationToken)
    {
        byte[] frame = new byte[UnixDatagramReceiver.MaximumDatagramSize];
        int frameLength = await receiver.ReceiveAsync(frame, cancellationToken);
        Assert.Equal(
            OperationStatus.Done,
            FrameCodec.TryDecode(
                frame.AsSpan(0, frameLength),
                out FrameHeader header,
                out _));
        return header;
    }

    private static void AssertCommit(FrameHeader header, Guid sessionId, long sequenceId)
    {
        Assert.Equal(MessageType.CommitThrough, header.MessageType);
        Assert.Equal(sessionId, header.SessionId);
        Assert.Equal(FrameCodec.ControlProducerId, header.ProducerId);
        Assert.Equal(0uL, header.ProducerSequence);
        Assert.Equal(sequenceId, header.SequenceId);
    }

    private static async Task<FrameHeader> ReceiveCandidateAsync(
        UnixStreamSocket stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(uint)];
        await stream.ReceiveExactlyAsync(prefix, cancellationToken);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(prefix));

        await stream.ReceiveExactlyAsync(prefix, cancellationToken);
        int frameLength = FrameCodec.ReadFrameLength(prefix);
        byte[] frame = new byte[frameLength];
        prefix.CopyTo(frame, 0);
        await stream.ReceiveExactlyAsync(frame.AsMemory(sizeof(uint)), cancellationToken);

        return FrameCodec.DecodeSequencedCandidate(frame).Header;
    }

    private static async Task AssertNoMulticastAsync(
        UdpMulticastReceiver receiver,
        CancellationToken cancellationToken)
    {
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        silence.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await receiver.ReceiveAsync(
                new byte[UnixDatagramReceiver.MaximumDatagramSize],
                silence.Token));
    }

    private static async Task CancelAsync(
        CancellationTokenSource cancellation,
        params Task[] tasks)
    {
        await cancellation.CancelAsync();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static byte[] EncodeStart(
        ushort producerId,
        ulong producerSequence,
        Guid sessionId)
    {
        return EncodeSubmission(MessageType.StartNewSession, sessionId, producerId, producerSequence, []);
    }

    private static byte[] EncodeSubmission(
        MessageType messageType,
        Guid sessionId,
        ushort producerId,
        ulong producerSequence,
        ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize + payload.Length];
        FrameCodec.Encode(messageType, sessionId, producerId, producerSequence, 0, payload, frame);
        return frame;
    }

    private static byte[] EncodeInvalidAcknowledgement(
        InvalidAcknowledgement invalidAcknowledgement,
        Guid sessionId)
    {
        switch (invalidAcknowledgement)
        {
            case InvalidAcknowledgement.Length:
                byte[] invalidLength = CommitThroughCodec.Encode(sessionId, 1).Bytes.ToArray();
                BinaryPrimitives.WriteUInt32BigEndian(
                    invalidLength,
                    FrameCodec.MinimumFrameSize - 1);
                return invalidLength;
            case InvalidAcknowledgement.Checksum:
                byte[] invalidChecksum = CommitThroughCodec.Encode(sessionId, 1).Bytes.ToArray();
                invalidChecksum[^1] ^= 0xff;
                return invalidChecksum;
            case InvalidAcknowledgement.MessageType:
                return FrameCodec.Encode(
                    MessageType.PlaceOrder,
                    sessionId,
                    FrameCodec.ControlProducerId,
                    0,
                    1,
                    []).Bytes.ToArray();
            case InvalidAcknowledgement.ProducerId:
                return FrameCodec.Encode(MessageType.CommitThrough, sessionId, 1, 0, 1, []).Bytes.ToArray();
            case InvalidAcknowledgement.ProducerSequence:
                return FrameCodec.Encode(
                    MessageType.CommitThrough,
                    sessionId,
                    FrameCodec.ControlProducerId,
                    1,
                    1,
                    []).Bytes.ToArray();
            case InvalidAcknowledgement.Payload:
                return FrameCodec.Encode(
                    MessageType.CommitThrough,
                    sessionId,
                    FrameCodec.ControlProducerId,
                    0,
                    1,
                    [0x01]).Bytes.ToArray();
            case InvalidAcknowledgement.HighWater:
                return CommitThroughCodec.Encode(sessionId, 2).Bytes.ToArray();
            case InvalidAcknowledgement.SessionId:
                return CommitThroughCodec.Encode(
                    new Guid("708192a3-b4c5-d6e7-f809-1a2b3c4d5e6f"),
                    1).Bytes.ToArray();
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidAcknowledgement));
        }
    }

    private static int GetUnusedPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public enum InvalidAcknowledgement
    {
        Length,
        Checksum,
        MessageType,
        ProducerId,
        ProducerSequence,
        Payload,
        HighWater,
        SessionId,
    }
}
