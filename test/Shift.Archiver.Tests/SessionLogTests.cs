using System.Buffers.Binary;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Archiver.Tests;

public sealed class SessionLogTests : IDisposable
{
    private static readonly Guid _messageId = new("00112233-4455-6677-8899-aabbccddeeff");
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
    public void CommitWritesCanonicalFrameAndMarker()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        byte[] frame = EncodeFrame(1);
        using SessionLog log = new(path);

        log.Append(frame);
        long committedThrough = log.Commit();

        byte[] expected = new byte[frame.Length + _flushBoundaryThroughOne.Length];
        frame.CopyTo(expected, 0);
        _flushBoundaryThroughOne.CopyTo(expected, frame.Length);
        Assert.Equal(1, committedThrough);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void CommitIncludesEveryPendingFrame()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        using SessionLog log = new(path);

        log.Append(EncodeFrame(1));
        log.Append(EncodeFrame(2));

        Assert.Equal(2, log.Commit());
    }

    [Fact]
    public void SequenceContinuesAcrossFlushBoundaries()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        byte[] firstFrame = EncodeFrame(1);
        byte[] secondFrame = EncodeFrame(2);
        using SessionLog log = new(path);

        log.Append(firstFrame);
        Assert.Equal(1, log.Commit());
        log.Append(secondFrame);
        Assert.Equal(2, log.Commit());

        byte[] bytes = File.ReadAllBytes(path);
        int secondMarkerOffset = firstFrame.Length
            + _flushBoundaryThroughOne.Length
            + secondFrame.Length;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(secondMarkerOffset)));
        Assert.Equal(
            2,
            BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(secondMarkerOffset + sizeof(uint))));

        ReadOnlySpan<byte> marker = bytes.AsSpan(secondMarkerOffset, _flushBoundaryThroughOne.Length);
        Assert.Equal(
            Crc32C.Compute(marker[..^sizeof(uint)]),
            BinaryPrimitives.ReadUInt32BigEndian(marker[^sizeof(uint)..]));
    }

    [Fact]
    public void AppendRequiresContiguousSequencesStartingAtOne()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        using SessionLog log = new(path);

        Assert.Throws<InvalidDataException>(() => log.Append(EncodeFrame(0)));
        log.Append(EncodeFrame(1));
        Assert.Throws<InvalidDataException>(() => log.Append(EncodeFrame(1)));
        Assert.Throws<InvalidDataException>(() => log.Append(EncodeFrame(3)));
        log.Append(EncodeFrame(2));

        Assert.Equal(2, log.Commit());
    }

    [Fact]
    public void AppendRejectsInvalidFrames()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        byte[] corrupt = EncodeFrame(1);
        corrupt[FrameCodec.HeaderSize] ^= 0xff;
        using SessionLog log = new(path);

        Assert.Throws<InvalidDataException>(() => log.Append(corrupt));
        Assert.Throws<InvalidDataException>(() => log.Append(corrupt.AsSpan(0, corrupt.Length - 1)));
        log.Append(EncodeFrame(1));

        Assert.Equal(1, log.Commit());
    }

    [Fact]
    public void AppendRejectsCommitThroughFrames()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        using SessionLog log = new(path);

        Assert.Throws<InvalidDataException>(() =>
            log.Append(EncodeFrame(1, MessageType.CommitThrough)));
        log.Append(EncodeFrame(1));

        Assert.Equal(1, log.Commit());
    }

    [Fact]
    public void CommitRejectsNoPendingFrames()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        using SessionLog log = new(path);

        Assert.Throws<InvalidOperationException>(() => log.Commit());
        log.Append(EncodeFrame(1));
        Assert.Equal(1, log.Commit());
        Assert.Throws<InvalidOperationException>(() => log.Commit());
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

    private static byte[] EncodeFrame(long sequenceId, MessageType messageType = MessageType.PlaceOrder)
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize + _payload.Length];
        FrameCodec.Encode(messageType, _messageId, sequenceId, _payload, frame);
        return frame;
    }
}
