using System.Buffers.Binary;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Archiver.Tests;

public sealed class SessionArchiveTests : IDisposable
{
    private static readonly Guid _firstSessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid _secondSessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    private readonly string _archiveRoot;

    public SessionArchiveTests()
    {
        _archiveRoot = Path.Combine(Path.GetTempPath(), $"shift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_archiveRoot);
    }

    [Fact]
    public void CommitsAndRotatesSessionLogs()
    {
        CanonicalFrame firstStart = EncodeStart(_firstSessionId, 1);
        CanonicalFrame order = EncodeFrame(MessageType.PlaceOrder, _firstSessionId, 2);
        CanonicalFrame firstEnd = EncodeFrame(
            MessageType.EndCurrentSession,
            _firstSessionId,
            3,
            []);
        CanonicalFrame secondStart = EncodeStart(_secondSessionId, 1);
        CanonicalFrame secondEnd = EncodeFrame(
            MessageType.EndCurrentSession,
            _secondSessionId,
            2,
            []);
        using SessionArchive archive = new(_archiveRoot);

        Assert.Equal(1, archive.CommitBatch([firstStart]));
        Assert.Equal(3, archive.CommitBatch([order, firstEnd]));
        Assert.Equal(2, archive.CommitBatch([secondStart, secondEnd]));

        AssertLog(LogPath(_firstSessionId), [firstStart], [order, firstEnd]);
        AssertLog(LogPath(_secondSessionId), [secondStart, secondEnd]);
    }

    [Fact]
    public void RejectsBatchWithoutStartBeforeCreatingLog()
    {
        using SessionArchive archive = new(_archiveRoot);

        Assert.Throws<InvalidDataException>(() =>
            archive.CommitBatch([EncodeFrame(MessageType.PlaceOrder, _firstSessionId, 1)]));

        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public void RejectsInvalidStartBeforeCreatingLog()
    {
        CanonicalFrame invalidStart = FrameCodec.Encode(
            MessageType.StartNewSession,
            _firstSessionId,
            1,
            1,
            1,
            new byte[16]);
        using SessionArchive archive = new(_archiveRoot);

        Assert.Throws<InvalidDataException>(() => archive.CommitBatch([invalidStart]));

        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    [Fact]
    public void RejectsSecondStartWithoutChangingOpenLog()
    {
        CanonicalFrame firstStart = EncodeStart(_firstSessionId, 1);
        using SessionArchive archive = new(_archiveRoot);
        archive.CommitBatch([firstStart]);
        byte[] before = File.ReadAllBytes(LogPath(_firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            archive.CommitBatch([EncodeStart(_secondSessionId, 2)]));

        Assert.Equal(before, File.ReadAllBytes(LogPath(_firstSessionId)));
        Assert.False(File.Exists(LogPath(_secondSessionId)));
    }

    [Fact]
    public void RejectsSequenceGapInLaterFrameWithoutChangingOpenLog()
    {
        CanonicalFrame start = EncodeStart(_firstSessionId, 1);
        using SessionArchive archive = new(_archiveRoot);
        archive.CommitBatch([start]);
        byte[] before = File.ReadAllBytes(LogPath(_firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            archive.CommitBatch(
            [
                EncodeFrame(MessageType.PlaceOrder, _firstSessionId, 2),
                EncodeFrame(MessageType.PlaceOrder, _firstSessionId, 4),
            ]));

        Assert.Equal(before, File.ReadAllBytes(LogPath(_firstSessionId)));
    }

    [Fact]
    public void RejectsFrameFromAnotherSessionWithoutChangingOpenLog()
    {
        CanonicalFrame start = EncodeStart(_firstSessionId, 1);
        using SessionArchive archive = new(_archiveRoot);
        archive.CommitBatch([start]);
        byte[] before = File.ReadAllBytes(LogPath(_firstSessionId));

        Assert.Throws<InvalidDataException>(() =>
            archive.CommitBatch([EncodeFrame(MessageType.PlaceOrder, _secondSessionId, 2)]));

        Assert.Equal(before, File.ReadAllBytes(LogPath(_firstSessionId)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsInvalidEndBeforeCreatingLog(bool payloadNotEmpty)
    {
        CanonicalFrame start = EncodeStart(_firstSessionId, 1);
        CanonicalFrame end = EncodeFrame(
            MessageType.EndCurrentSession,
            _firstSessionId,
            2,
            payloadNotEmpty ? [0x01] : []);
        CanonicalFrame[] frames = payloadNotEmpty
            ? [start, end]
            : [start, end, EncodeFrame(MessageType.PlaceOrder, _firstSessionId, 3)];
        using SessionArchive archive = new(_archiveRoot);

        Assert.Throws<InvalidDataException>(() => archive.CommitBatch(frames));

        Assert.Empty(Directory.EnumerateFiles(_archiveRoot));
    }

    public void Dispose()
    {
        Directory.Delete(_archiveRoot, recursive: true);
    }

    private string LogPath(Guid sessionId)
    {
        return Path.Combine(_archiveRoot, $"{sessionId:N}.shiftlog");
    }

    private static CanonicalFrame EncodeStart(Guid sessionId, long sequenceId)
    {
        return FrameCodec.Encode(
            MessageType.StartNewSession,
            sessionId,
            1,
            (ulong)sequenceId,
            sequenceId,
            []);
    }

    private static CanonicalFrame EncodeFrame(
        MessageType messageType,
        Guid sessionId,
        long sequenceId,
        byte[]? payload = null)
    {
        return FrameCodec.Encode(
            messageType,
            sessionId,
            1,
            (ulong)sequenceId,
            sequenceId,
            payload ?? [0xde, 0xad]);
    }

    private static void AssertLog(string path, params CanonicalFrame[][] committedBatches)
    {
        const int CommitMarkerSize = sizeof(uint) + sizeof(long) + sizeof(uint);

        byte[] contents = File.ReadAllBytes(path);
        int offset = 0;
        foreach (CanonicalFrame[] batch in committedBatches)
        {
            foreach (CanonicalFrame frame in batch)
            {
                Assert.Equal(
                    frame.Bytes.Span,
                    contents.AsSpan(offset, frame.Bytes.Length));
                offset += frame.Bytes.Length;
            }

            ReadOnlySpan<byte> marker = contents.AsSpan(offset, CommitMarkerSize);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(marker));
            Assert.Equal(
                batch[^1].Header.SequenceId,
                BinaryPrimitives.ReadInt64BigEndian(marker[sizeof(uint)..]));
            Assert.Equal(
                Crc32C.Compute(marker[..^sizeof(uint)]),
                BinaryPrimitives.ReadUInt32BigEndian(marker[^sizeof(uint)..]));
            offset += CommitMarkerSize;
        }

        Assert.Equal(contents.Length, offset);
    }
}
