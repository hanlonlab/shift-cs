using System.Buffers.Binary;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Archiver.Tests;

public sealed class SessionLogTests : IDisposable
{
    private static readonly Guid _sessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly byte[] _payload = [0xde, 0xad, 0xbe, 0xef];
    private static readonly byte[] _flushBoundaryThroughOne =
    [
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
        0xd9, 0x0b, 0x36, 0x5e,
    ];

    private readonly string _directory;

    public SessionLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"shift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void CommitBatchWritesExactFrameAndMarker()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        CanonicalFrame frame = EncodeFrame(1);
        using SessionLog log = new(path);

        log.CommitBatch([frame], 1);

        byte[] expected = new byte[frame.Bytes.Length + _flushBoundaryThroughOne.Length];
        frame.Bytes.CopyTo(expected);
        _flushBoundaryThroughOne.CopyTo(expected, frame.Bytes.Length);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void CommitBatchWritesEveryFrameBeforeOneMarker()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        CanonicalFrame first = EncodeFrame(1);
        CanonicalFrame second = EncodeFrame(2);
        using SessionLog log = new(path);

        log.CommitBatch([first, second], 2);

        byte[] contents = File.ReadAllBytes(path);
        Assert.Equal(first.Bytes.Span, contents.AsSpan(0, first.Bytes.Length));
        Assert.Equal(
            second.Bytes.Span,
            contents.AsSpan(first.Bytes.Length, second.Bytes.Length));
        AssertMarker(contents.AsSpan(first.Bytes.Length + second.Bytes.Length), 2);
    }

    [Fact]
    public void CommitBatchAppendsAndFlushesEachBatch()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        CanonicalFrame first = EncodeFrame(1);
        CanonicalFrame second = EncodeFrame(2);
        using SessionLog log = new(path);

        log.CommitBatch([first], 1);

        byte[] firstCommit = File.ReadAllBytes(path);
        Assert.Equal(first.Bytes.Span, firstCommit.AsSpan(0, first.Bytes.Length));
        AssertMarker(firstCommit.AsSpan(first.Bytes.Length), 1);

        log.CommitBatch([second], 2);

        byte[] secondCommit = File.ReadAllBytes(path);
        int secondFrameOffset = firstCommit.Length;
        Assert.Equal(
            second.Bytes.Span,
            secondCommit.AsSpan(secondFrameOffset, second.Bytes.Length));
        AssertMarker(secondCommit.AsSpan(secondFrameOffset + second.Bytes.Length), 2);
    }

    [Fact]
    public void IoFailurePermanentlyFaultsLog()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        CanonicalFrame frame = EncodeFrame(1);
        using SessionLog log = new(path);
        log.Dispose();

        Assert.Throws<ObjectDisposedException>(() => log.CommitBatch([frame], 1));
        Assert.Throws<InvalidOperationException>(() => log.CommitBatch([frame], 1));
    }

    [Fact]
    public void ConstructorRejectsExistingFile()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        File.WriteAllBytes(path, [0x01]);

        Assert.Throws<IOException>(() => new SessionLog(path));
    }

    [Fact]
    public void ConstructorRequiresExistingParentDirectory()
    {
        string path = Path.Combine(_directory, "missing", "session.shiftlog");

        Assert.Throws<DirectoryNotFoundException>(() => new SessionLog(path));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static CanonicalFrame EncodeFrame(long sequenceId)
    {
        return FrameCodec.Encode(
            MessageType.PlaceOrder,
            _sessionId,
            1,
            (ulong)sequenceId,
            sequenceId,
            _payload);
    }

    private static void AssertMarker(ReadOnlySpan<byte> marker, long highWater)
    {
        Assert.Equal(sizeof(uint) + sizeof(long) + sizeof(uint), marker.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(marker));
        Assert.Equal(highWater, BinaryPrimitives.ReadInt64BigEndian(marker[sizeof(uint)..]));
        Assert.Equal(
            Crc32C.Compute(marker[..^sizeof(uint)]),
            BinaryPrimitives.ReadUInt32BigEndian(marker[^sizeof(uint)..]));
    }
}
