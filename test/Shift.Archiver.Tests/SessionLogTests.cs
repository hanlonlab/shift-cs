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
    public void IoFailurePermanentlyFaultsLog()
    {
        string path = Path.Combine(_directory, "session.shiftlog");
        CanonicalFrame frame = EncodeFrame(1);
        using SessionLog log = new(path);
        log.Dispose();

        Assert.Throws<ObjectDisposedException>(() => log.CommitBatch([frame], 1));
        Assert.Throws<InvalidOperationException>(() => log.CommitBatch([frame], 1));
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
}
