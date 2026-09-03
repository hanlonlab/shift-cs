using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Shift.Archiver;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
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
            using ArchiverServer archiver = new(
                archiveRoot,
                await listener.AcceptAsync(timeout.Token));
            Task archiverTask = archiver.RunAsync(timeout.Token);
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
                Assert.Equal(1, firstStart.SequenceId);
                AssertCommit(firstStartCommit, 1);

                await submissions.SendAsync(
                    EncodeSubmission(MessageType.PlaceOrder, ProducerId, 2, [0x01]),
                    timeout.Token);
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.EndCurrentSession, ProducerId, 3, []),
                    timeout.Token);

                FrameHeader order = await ReceiveAsync(committed, timeout.Token);
                FrameHeader firstEnd = await ReceiveAsync(committed, timeout.Token);
                FrameHeader firstEndCommit = await ReceiveAsync(committed, timeout.Token);
                Assert.Equal(MessageType.PlaceOrder, order.MessageType);
                Assert.Equal(2, order.SequenceId);
                Assert.Equal(MessageType.EndCurrentSession, firstEnd.MessageType);
                Assert.Equal(3, firstEnd.SequenceId);
                AssertCommit(firstEndCommit, 3);

                Guid secondSessionId = new("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");
                await submissions.SendAsync(
                    EncodeStart(ProducerId, 1, secondSessionId),
                    timeout.Token);

                FrameHeader secondStart = await ReceiveAsync(committed, timeout.Token);
                FrameHeader secondStartCommit = await ReceiveAsync(committed, timeout.Token);
                Assert.Equal(MessageType.StartNewSession, secondStart.MessageType);
                Assert.Equal(1, secondStart.SequenceId);
                AssertCommit(secondStartCommit, 1);

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

    [Fact]
    public async Task DoesNotMulticastBeforeTheExactDurableAcknowledgement()
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
                await submissions.SendAsync(
                    EncodeStart(
                        ProducerId,
                        1,
                        new Guid("60718293-a4b5-c6d7-e8f9-0a1b2c3d4e5f")),
                    timeout.Token);

                FrameHeader candidate = await ReceiveCandidateAsync(stream, timeout.Token);
                Assert.Equal(MessageType.StartNewSession, candidate.MessageType);
                Assert.Equal(1, candidate.SequenceId);
                await AssertNoMulticastAsync(committed, timeout.Token);

                byte[] wrongAcknowledgement = new byte[FrameCodec.MinimumFrameSize];
                FrameCodec.Encode(
                    MessageType.CommitThrough,
                    FrameCodec.ControlProducerId,
                    0,
                    2,
                    ReadOnlySpan<byte>.Empty,
                    wrongAcknowledgement);
                await stream.SendExactlyAsync(wrongAcknowledgement, timeout.Token);

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

    private static void AssertCommit(FrameHeader header, long sequenceId)
    {
        Assert.Equal(MessageType.CommitThrough, header.MessageType);
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
        int frameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(prefix));
        byte[] frame = new byte[frameLength];
        prefix.CopyTo(frame, 0);
        await stream.ReceiveExactlyAsync(frame.AsMemory(sizeof(uint)), cancellationToken);

        Assert.Equal(
            OperationStatus.Done,
            FrameCodec.TryDecode(frame, out FrameHeader header, out _));
        return header;
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
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(sessionId), payload);
        return EncodeSubmission(MessageType.StartNewSession, producerId, producerSequence, payload);
    }

    private static byte[] EncodeSubmission(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize + payload.Length];
        FrameCodec.Encode(messageType, producerId, producerSequence, 0, payload, frame);
        return frame;
    }

    private static int GetUnusedPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
