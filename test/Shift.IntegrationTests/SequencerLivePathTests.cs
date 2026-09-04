using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Shift.Archiver;
using Shift.Ipc;
using Shift.Protocol;
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
        var group = IPAddress.Parse("239.255.43.1");
        int port = GetUnusedPort();
        Directory.CreateDirectory(archiveRoot);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            using UdpMulticastReceiver committed = new(group, port, IPAddress.Loopback);
            using UnixDatagramReceiver submissionReceiver = new(submissionPath);
            using SessionArchive archiver = new(archiveRoot);
            using UdpMulticastSender multicast = new(group, port, IPAddress.Loopback);
            SequencerServer sequencer = new(submissionReceiver, archiver, multicast);
            Task sequencerTask = sequencer.RunAsync(timeout.Token);
            using UnixDatagramSender submissions = new(submissionPath);

            try
            {
                Guid firstSessionId = new("00112233-4455-6677-8899-aabbccddeeff");
                CanonicalFrame firstStart = FrameCodec.Encode(
                    MessageType.StartNewSession, firstSessionId, ProducerId, 1, 1, []);
                CanonicalFrame firstStartCommit = CommitThroughCodec.Encode(firstSessionId, 1);
                byte[] firstStartSubmission = EncodeSubmission(
                    MessageType.StartNewSession, firstSessionId, ProducerId, 1, []);
                await submissions.SendAsync(firstStartSubmission, timeout.Token);
                Assert.Equal(firstStart.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));
                Assert.Equal(firstStartCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                await submissions.SendAsync(firstStartSubmission, timeout.Token);
                Assert.Equal(firstStart.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));
                Assert.Equal(firstStartCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                CanonicalFrame order = FrameCodec.Encode(
                    MessageType.PlaceOrder, firstSessionId, ProducerId, 2, 2, [0x01]);
                CanonicalFrame orderCommit = CommitThroughCodec.Encode(firstSessionId, 2);
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.PlaceOrder, firstSessionId, ProducerId, 2, [0x01]),
                    timeout.Token);
                Assert.Equal(order.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));
                Assert.Equal(orderCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                await submissions.SendAsync(firstStartSubmission, timeout.Token);
                Assert.Equal(orderCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                CanonicalFrame firstEnd = FrameCodec.Encode(
                    MessageType.EndCurrentSession, firstSessionId, ProducerId, 3, 3, []);
                CanonicalFrame firstEndCommit = CommitThroughCodec.Encode(firstSessionId, 3);
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.EndCurrentSession, firstSessionId, ProducerId, 3, []),
                    timeout.Token);
                Assert.Equal(firstEnd.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));
                Assert.Equal(firstEndCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                Guid secondSessionId = new("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");
                CanonicalFrame secondStart = FrameCodec.Encode(
                    MessageType.StartNewSession, secondSessionId, ProducerId, 1, 1, []);
                CanonicalFrame secondStartCommit = CommitThroughCodec.Encode(secondSessionId, 1);
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.StartNewSession, secondSessionId, ProducerId, 1, []),
                    timeout.Token);
                Assert.Equal(secondStart.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));
                Assert.Equal(secondStartCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                await submissions.SendAsync(
                    EncodeSubmission(MessageType.PlaceOrder, firstSessionId, ProducerId, 2, [0x01]),
                    timeout.Token);
                await AssertNoMulticastAsync(committed, timeout.Token);

                CanonicalFrame secondEnd = FrameCodec.Encode(
                    MessageType.EndCurrentSession, secondSessionId, ProducerId, 2, 2, []);
                CanonicalFrame secondEndCommit = CommitThroughCodec.Encode(secondSessionId, 2);
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.EndCurrentSession, secondSessionId, ProducerId, 2, []),
                    timeout.Token);
                Assert.Equal(secondEnd.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));
                Assert.Equal(secondEndCommit.Bytes.ToArray(), await ReceiveAsync(committed, timeout.Token));

                AssertArchive(
                    Path.Combine(archiveRoot, $"{firstSessionId:N}.shiftlog"),
                    [firstStart, firstStartCommit, order, orderCommit, firstEnd, firstEndCommit]);
                AssertArchive(
                    Path.Combine(archiveRoot, $"{secondSessionId:N}.shiftlog"),
                    [secondStart, secondStartCommit, secondEnd, secondEndCommit]);
            }
            finally
            {
                await CancelAsync(timeout, sequencerTask);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArchiveFailureStopsSequencerWithoutMulticastingOrOverwritingExistingLog()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX integration test requires macOS or Linux.");

        string directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        string submissionPath = Path.Combine(directory, "in.sock");
        var group = IPAddress.Parse("239.255.43.2");
        int port = GetUnusedPort();
        Directory.CreateDirectory(directory);
        Guid sessionId = new("60718293-a4b5-c6d7-e8f9-0a1b2c3d4e5f");
        string logPath = Path.Combine(directory, $"{sessionId:N}.shiftlog");
        byte[] existingBytes = [0x01, 0x02, 0x03];

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            using UdpMulticastReceiver committed = new(group, port, IPAddress.Loopback);
            using UnixDatagramReceiver submissionReceiver = new(submissionPath);
            await File.WriteAllBytesAsync(logPath, existingBytes, timeout.Token);
            using SessionArchive archiver = new(directory);
            using UdpMulticastSender multicast = new(group, port, IPAddress.Loopback);
            SequencerServer sequencer = new(submissionReceiver, archiver, multicast);
            Task sequencerTask = sequencer.RunAsync(timeout.Token);
            using UnixDatagramSender submissions = new(submissionPath);

            try
            {
                await submissions.SendAsync(
                    EncodeSubmission(MessageType.StartNewSession, sessionId, ProducerId, 1, []),
                    timeout.Token);

                await Assert.ThrowsAsync<IOException>(async () => await sequencerTask);
                await AssertNoMulticastAsync(committed, timeout.Token);
                Assert.Equal(existingBytes, await File.ReadAllBytesAsync(logPath, timeout.Token));
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

    [Fact]
    public async Task QueuedSubmissionsKeepTheirBytesWhenTheReceiveBufferIsReused()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX integration test requires macOS or Linux.");

        string directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        string submissionPath = Path.Combine(directory, "in.sock");
        var group = IPAddress.Parse("239.255.43.3");
        int port = GetUnusedPort();
        Directory.CreateDirectory(directory);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            using UdpMulticastReceiver committed = new(group, port, IPAddress.Loopback);
            using UnixDatagramReceiver submissionReceiver = new(submissionPath);
            using SessionArchive archiver = new(directory);
            using UdpMulticastSender multicast = new(group, port, IPAddress.Loopback);
            using UnixDatagramSender submissions = new(submissionPath);
            var sessionId = Guid.NewGuid();
            byte[] payload = new byte[FrameCodec.MaximumFrameSize - FrameCodec.MinimumFrameSize];
            Array.Fill(payload, (byte)0x7f);
            CanonicalFrame[] expected =
            [
                FrameCodec.Encode(MessageType.StartNewSession, sessionId, ProducerId, 1, 1, []),
                FrameCodec.Encode(MessageType.PlaceOrder, sessionId, ProducerId, 2, 2, payload),
                FrameCodec.Encode(MessageType.PlaceOrder, sessionId, ProducerId, 3, 3, [0x01, 0x02]),
                FrameCodec.Encode(MessageType.EndCurrentSession, sessionId, ProducerId, 4, 4, []),
            ];
            foreach (CanonicalFrame frame in expected)
            {
                await submissions.SendAsync(
                    EncodeSubmission(
                        frame.Header.MessageType,
                        sessionId,
                        ProducerId,
                        frame.Header.ProducerSequence,
                        frame.Payload.Span),
                    timeout.Token);
            }

            SequencerServer sequencer = new(submissionReceiver, archiver, multicast);
            Task sequencerTask = sequencer.RunAsync(timeout.Token);
            try
            {
                List<CanonicalFrame> archived = [];
                int received = 0;
                while (true)
                {
                    byte[] bytes = await ReceiveAsync(committed, timeout.Token);
                    Assert.Equal(
                        OperationStatus.Done,
                        FrameCodec.TryDecode(bytes, out FrameHeader header, out _));
                    if (header.MessageType == MessageType.CommitThrough)
                    {
                        CanonicalFrame watermark = CommitThroughCodec.Encode(sessionId, received);
                        Assert.Equal(watermark.Bytes.ToArray(), bytes);
                        archived.Add(watermark);
                        if (received == expected.Length)
                        {
                            break;
                        }
                    }
                    else
                    {
                        Assert.True(received < expected.Length);
                        Assert.Equal(expected[received].Bytes.ToArray(), bytes);
                        archived.Add(expected[received++]);
                    }
                }

                AssertArchive(Path.Combine(directory, $"{sessionId:N}.shiftlog"), archived);
            }
            finally
            {
                await CancelAsync(timeout, sequencerTask);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<byte[]> ReceiveAsync(
        UdpMulticastReceiver receiver,
        CancellationToken cancellationToken)
    {
        byte[] frame = new byte[UnixDatagramReceiver.MaximumDatagramSize];
        int frameLength = await receiver.ReceiveAsync(frame, cancellationToken);
        return frame[..frameLength];
    }

    private static void AssertArchive(string path, IEnumerable<CanonicalFrame> frames)
    {
        using MemoryStream expected = new();
        Span<byte> marker = stackalloc byte[16];
        foreach (CanonicalFrame frame in frames)
        {
            if (frame.Header.MessageType == MessageType.CommitThrough)
            {
                BinaryPrimitives.WriteUInt32BigEndian(marker, 0);
                BinaryPrimitives.WriteInt64BigEndian(marker[4..], frame.Header.SequenceId);
                BinaryPrimitives.WriteUInt32BigEndian(marker[12..], Crc32C.Compute(marker[..12]));
                expected.Write(marker);
            }
            else
            {
                expected.Write(frame.Bytes.Span);
            }
        }

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
    }

    private static async Task AssertNoMulticastAsync(
        UdpMulticastReceiver receiver,
        CancellationToken cancellationToken)
    {
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        silence.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await receiver.ReceiveAsync(
                new byte[UnixDatagramReceiver.MaximumDatagramSize],
                silence.Token));
    }

    private static async Task CancelAsync(CancellationTokenSource cancellation, Task task)
    {
        await cancellation.CancelAsync();
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static byte[] EncodeSubmission(
        MessageType messageType,
        Guid sessionId,
        ushort producerId,
        ulong producerSequence,
        ReadOnlySpan<byte> payload)
    {
        return FrameCodec.Encode(messageType, sessionId, producerId, producerSequence, 0, payload).Bytes.ToArray();
    }

    private static int GetUnusedPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
