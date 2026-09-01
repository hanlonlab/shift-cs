using System.Buffers;
using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Archiver.Tests;

public sealed class ArchiverServerTests : IDisposable
{
    private static readonly Guid _firstSessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid _secondSessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid _firstMessageId = new("20314253-6475-8697-a8b9-cadbecfd0e1f");
    private static readonly Guid _secondMessageId = new("30415263-7485-96a7-b8c9-daebfc0d1e2f");
    private static readonly Guid _thirdMessageId = new("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");

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

        byte[] firstStart = EncodeStart(_firstMessageId, _firstSessionId, 1);
        await SendBatchAsync(connection.Client, firstStart);
        await AssertAcknowledgementAsync(connection.Client, 1);

        byte[] order = EncodeFrame(MessageType.PlaceOrder, _secondMessageId, 2, [0xde, 0xad]);
        byte[] firstEnd = EncodeFrame(MessageType.EndCurrentSession, _thirdMessageId, 3, []);
        await SendBatchAsync(connection.Client, order, firstEnd);
        await AssertAcknowledgementAsync(connection.Client, 3);

        byte[] secondStart = EncodeStart(_secondMessageId, _secondSessionId, 1);
        byte[] secondEnd = EncodeFrame(MessageType.EndCurrentSession, _thirdMessageId, 2, []);
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
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);

        await SendBatchAsync(
            connection.Client,
            EncodeFrame(MessageType.PlaceOrder, _firstMessageId, 1, []));

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsSecondStartWhileSessionIsOpen()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);

        byte[] firstStart = EncodeStart(_firstMessageId, _firstSessionId, 1);
        await SendBatchAsync(connection.Client, firstStart);
        await AssertAcknowledgementAsync(connection.Client, 1);

        await SendBatchAsync(
            connection.Client,
            EncodeStart(_secondMessageId, _secondSessionId, 2));

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
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);

        byte[] start = EncodeStart(_firstMessageId, _firstSessionId, 1);
        byte[] end = EncodeFrame(
            MessageType.EndCurrentSession,
            _secondMessageId,
            2,
            payloadNotEmpty ? [0x01] : []);
        byte[][] frames = payloadNotEmpty
            ? [start, end]
            : [
                start,
                end,
                EncodeFrame(MessageType.PlaceOrder, _thirdMessageId, 3, []),
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
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);
        Guid messageId = emptyCandidateId ? Guid.Empty : _firstMessageId;
        Guid sessionId = emptyCandidateId ? _firstSessionId : Guid.Empty;

        await SendBatchAsync(connection.Client, EncodeStart(messageId, sessionId, 1));

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public async Task RejectsFrameLargerThanTwoKibibytes()
    {
        AssertUnixSockets();

        using Connection connection = await Connection.CreateAsync(_socketPath);
        using ArchiverServer archiver = new(_archiveRoot);
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);
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
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);
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
        Task run = archiver.RunAsync(connection.Server, TestContext.Current.CancellationToken);
        byte[] maximumFrame = new byte[UnixDatagramReceiver.MaximumDatagramSize];
        BinaryPrimitives.WriteUInt32BigEndian(
            maximumFrame,
            UnixDatagramReceiver.MaximumDatagramSize);
        byte[][] frames = Enumerable.Repeat(maximumFrame, 513).ToArray();

        await SendBatchAsync(connection.Client, frames);

        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
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

    private static byte[] EncodeStart(Guid messageId, Guid sessionId, long sequenceId)
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(sessionId), payload);
        return EncodeFrame(MessageType.StartNewSession, messageId, sequenceId, payload);
    }

    private static byte[] EncodeFrame(
        MessageType messageType,
        Guid messageId,
        long sequenceId,
        byte[] payload)
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize + payload.Length];
        FrameCodec.Encode(messageType, messageId, sequenceId, payload, frame);
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

        Assert.Equal(
            OperationStatus.Done,
            FrameCodec.TryDecode(
                acknowledgement,
                out FrameHeader header,
                out ReadOnlySpan<byte> payload));
        Assert.Equal(MessageType.CommitThrough, header.MessageType);
        Assert.Equal(Guid.Empty, header.MessageId);
        Assert.Equal(sequenceId, header.SequenceId);
        Assert.True(payload.IsEmpty);
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
                OperationStatus.Done,
                FrameCodec.TryDecode(batch[^1], out FrameHeader header, out _));
            Assert.Equal(
                header.SequenceId,
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
            Server.Dispose();
            Client.Dispose();
            _listener.Dispose();
        }
    }
}
