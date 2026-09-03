using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Archiver.Tests;

public sealed class ArchiverServerTests : IDisposable
{
    private const ushort FirstProducerId = 1;
    private const ushort SecondProducerId = 2;
    private const ushort ThirdProducerId = 3;
    private static readonly Guid _firstSessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid _secondSessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    private readonly string _archiveRoot;
    private readonly string _directory;
    private readonly string _socketPath;

    public ArchiverServerTests()
    {
        string temporaryRoot = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        _directory = Path.Combine(temporaryRoot, $"shift-{Guid.NewGuid():N}");
        _archiveRoot = Path.Combine(_directory, "archive");
        _socketPath = Path.Combine(_directory, "archiver.sock");
        Directory.CreateDirectory(_archiveRoot);
    }

    [Fact]
    public async Task CommitsAndRotatesSessionLogs()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task run = archiver.RunAsync(connection.Server, stop.Token);

        byte[] firstStart = EncodeStart(FirstProducerId, 1, _firstSessionId, 1);
        await SendBatchAsync(connection.Client, firstStart);
        await AssertAcknowledgementAsync(connection.Client, 1);

        byte[] order = EncodeFrame(MessageType.PlaceOrder, SecondProducerId, 1, 2, [0xde, 0xad]);
        byte[] firstEnd = EncodeFrame(MessageType.EndCurrentSession, ThirdProducerId, 1, 3, []);
        await SendBatchAsync(connection.Client, order, firstEnd);
        await AssertAcknowledgementAsync(connection.Client, 3);

        byte[] secondStart = EncodeStart(SecondProducerId, 1, _secondSessionId, 1);
        byte[] secondEnd = EncodeFrame(MessageType.EndCurrentSession, ThirdProducerId, 1, 2, []);
        await SendBatchAsync(connection.Client, secondStart, secondEnd);
        await AssertAcknowledgementAsync(connection.Client, 2);

        AssertLog(
            Path.Combine(_archiveRoot, $"{_firstSessionId:N}.shiftlog"),
            [firstStart],
            [order, firstEnd]);
        AssertLog(
            Path.Combine(_archiveRoot, $"{_secondSessionId:N}.shiftlog"),
            [secondStart, secondEnd]);

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RejectsBatchWithoutStartWhenNoSessionIsOpen()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);

        await SendBatchAsync(
            connection.Client,
            EncodeFrame(MessageType.PlaceOrder, FirstProducerId, 1, 1, []));

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsSecondStartWhileSessionIsOpen()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);

        byte[] firstStart = EncodeStart(FirstProducerId, 1, _firstSessionId, 1);
        await SendBatchAsync(connection.Client, firstStart);
        await AssertAcknowledgementAsync(connection.Client, 1);

        await SendBatchAsync(
            connection.Client,
            EncodeStart(SecondProducerId, 1, _secondSessionId, 2));

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        AssertLog(Path.Combine(_archiveRoot, $"{_firstSessionId:N}.shiftlog"), [firstStart]);
        Assert.False(File.Exists(Path.Combine(_archiveRoot, $"{_secondSessionId:N}.shiftlog")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsInvalidEndBatchBeforeCreatingLog(bool payloadNotEmpty)
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);

        byte[] start = EncodeStart(FirstProducerId, 1, _firstSessionId, 1);
        byte[] end = EncodeFrame(
            MessageType.EndCurrentSession,
            SecondProducerId,
            1,
            2,
            payloadNotEmpty ? [0x01] : []);
        byte[][] frames = payloadNotEmpty
            ? [start, end]
            : [
                start,
                end,
                EncodeFrame(MessageType.PlaceOrder, ThirdProducerId, 1, 3, []),
            ];

        await SendBatchAsync(connection.Client, frames);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsEmptyCandidateOrSessionIdentity(bool emptyCandidateId)
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        ushort producerId = emptyCandidateId ? FrameCodec.ControlProducerId : FirstProducerId;
        byte[] start = emptyCandidateId
            ? EncodeStart(producerId, 1, _firstSessionId, 1)
            : EncodeFrame(MessageType.StartNewSession, producerId, 1, 1, new byte[16]);

        await SendBatchAsync(connection.Client, start);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    public async Task RejectsCandidateRoleViolations(
        int producerSequence,
        long sequenceId,
        bool commitThrough)
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        byte[] frame = commitThrough
            ? EncodeFrame(
                MessageType.CommitThrough,
                FirstProducerId,
                (ulong)producerSequence,
                sequenceId,
                [])
            : EncodeStart(
                FirstProducerId,
                (ulong)producerSequence,
                _firstSessionId,
                sequenceId);

        await SendBatchAsync(connection.Client, frame);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsFrameLargerThanTwoKibibytes()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        byte[] prefixes = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(prefixes, 1);
        BinaryPrimitives.WriteUInt32BigEndian(prefixes.AsSpan(sizeof(uint)), 2_049);

        await connection.Client.SendExactlyAsync(prefixes, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsImpossibleFrameCountBeforeReadingFrames()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        byte[] count = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(count, uint.MaxValue);

        await connection.Client.SendExactlyAsync(count, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsBatchLargerThanOneMebibyte()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        byte[] maximumFrame = FrameCodec.Encode(
            MessageType.PlaceOrder,
            FirstProducerId,
            1,
            1,
            new byte[FrameCodec.MaximumFrameSize - FrameCodec.MinimumFrameSize]).Bytes.ToArray();
        byte[][] frames = Enumerable.Repeat(maximumFrame, 513).ToArray();

        await SendBatchAsync(connection.Client, frames);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsCorruptLaterFrameBeforeCreatingLog()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        byte[] corrupt = EncodeFrame(
            MessageType.PlaceOrder,
            SecondProducerId,
            1,
            2,
            [0xde, 0xad]);
        corrupt[^1] ^= 0xff;

        await SendBatchAsync(
            connection.Client,
            EncodeStart(FirstProducerId, 1, _firstSessionId, 1),
            corrupt);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsCorruptLaterFrameWithoutChangingOpenLog()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(
            connection.Server,
            TestContext.Current.CancellationToken);
        byte[] start = EncodeStart(FirstProducerId, 1, _firstSessionId, 1);
        await SendBatchAsync(connection.Client, start);
        await AssertAcknowledgementAsync(connection.Client, 1);
        string path = Path.Combine(_archiveRoot, $"{_firstSessionId:N}.shiftlog");
        byte[] before = File.ReadAllBytes(path);
        byte[] corrupt = EncodeFrame(
            MessageType.PlaceOrder,
            ThirdProducerId,
            1,
            3,
            [0xbe, 0xef]);
        corrupt[^1] ^= 0xff;

        await SendBatchAsync(
            connection.Client,
            EncodeFrame(MessageType.PlaceOrder, SecondProducerId, 1, 2, [0xde, 0xad]),
            corrupt);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static void AssertUnixSockets()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX tests require macOS or Linux.");
    }

    private static byte[] EncodeStart(ushort producerId, ulong producerSequence, Guid sessionId, long sequenceId)
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(sessionId), payload);
        return EncodeFrame(MessageType.StartNewSession, producerId, producerSequence, sequenceId, payload);
    }

    private static byte[] EncodeFrame(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        byte[] payload)
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize + payload.Length];
        FrameCodec.Encode(messageType, producerId, producerSequence, sequenceId, payload, frame);
        return frame;
    }

    private static async Task SendBatchAsync(UnixStreamSocket socket, params byte[][] frames)
    {
        byte[] batch = new byte[sizeof(uint) + frames.Sum(frame => frame.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(batch, (uint)frames.Length);
        int offset = sizeof(uint);
        foreach (byte[] frame in frames)
        {
            frame.CopyTo(batch, offset);
            offset += frame.Length;
        }

        await socket.SendExactlyAsync(batch, TestContext.Current.CancellationToken);
    }

    private static async Task AssertAcknowledgementAsync(UnixStreamSocket socket, long sequenceId)
    {
        byte[] acknowledgement = new byte[FrameCodec.MinimumFrameSize];
        await socket.ReceiveExactlyAsync(acknowledgement, TestContext.Current.CancellationToken);

        CanonicalFrame commit = FrameCodec.DecodeCommitThrough(acknowledgement);
        Assert.Equal(sequenceId, commit.Header.SequenceId);
    }

    private static void AssertLog(string path, params byte[][][] committedBatches)
    {
        const int CommitMarkerSize = sizeof(uint) + sizeof(long) + sizeof(uint);

        byte[] contents = File.ReadAllBytes(path);
        int offset = 0;

        foreach (byte[][] batch in committedBatches)
        {
            foreach (byte[] frame in batch)
            {
                Assert.Equal(frame, contents.AsSpan(offset, frame.Length).ToArray());
                offset += frame.Length;
            }

            ReadOnlySpan<byte> marker = contents.AsSpan(offset, CommitMarkerSize);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(marker));
            Assert.Equal(
                FrameCodec.DecodeSequencedCandidate(batch[^1]).Header.SequenceId,
                BinaryPrimitives.ReadInt64BigEndian(marker[sizeof(uint)..]));
            Assert.Equal(
                Crc32C.Compute(marker[..^sizeof(uint)]),
                BinaryPrimitives.ReadUInt32BigEndian(marker[^sizeof(uint)..]));
            offset += CommitMarkerSize;
        }

        Assert.Equal(contents.Length, offset);
    }

    private sealed class Connection : IDisposable
    {
        private readonly UnixStreamSocket _listener;

        private Connection(
            UnixStreamSocket listener,
            UnixStreamSocket client,
            UnixStreamSocket server)
        {
            _listener = listener;
            Client = client;
            Server = server;
        }

        public UnixStreamSocket Client { get; }

        public UnixStreamSocket Server { get; }

        public static async Task<Connection> CreateAsync(string path)
        {
            UnixStreamSocket? listener = null;
            UnixStreamSocket? client = null;

            try
            {
                listener = UnixStreamSocket.Listen(path);
                client = await UnixStreamSocket.ConnectAsync(
                    path,
                    TestContext.Current.CancellationToken);
                UnixStreamSocket server = await listener.AcceptAsync(
                    TestContext.Current.CancellationToken);
                return new Connection(listener, client, server);
            }
            catch
            {
                client?.Dispose();
                listener?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            _listener.Dispose();
        }
    }
}
