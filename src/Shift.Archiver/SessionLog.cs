using System.Buffers.Binary;
using Shift.Protocol;
using Shift.Protocol.Framing;

namespace Shift.Archiver;

internal sealed class SessionLog : IDisposable
{
    private const uint CommitMarkerSentinel = 0;
    private const int CommitMarkerChecksumSize = sizeof(uint);
    private const int CommitMarkerSize = sizeof(uint) + sizeof(long) + CommitMarkerChecksumSize;

    private readonly FileStream _stream;
    private bool _faulted;

    public SessionLog(string path)
    {
        _stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
    }

    public void CommitBatch(ReadOnlySpan<CanonicalFrame> frames, long highWater)
    {
        ThrowIfFaulted();

        Span<byte> marker = stackalloc byte[CommitMarkerSize];
        BinaryPrimitives.WriteUInt32BigEndian(marker, CommitMarkerSentinel);
        BinaryPrimitives.WriteInt64BigEndian(marker[sizeof(uint)..], highWater);
        uint checksum = Crc32C.Compute(marker[..^CommitMarkerChecksumSize]);
        BinaryPrimitives.WriteUInt32BigEndian(
            marker[^CommitMarkerChecksumSize..],
            checksum);

        try
        {
            foreach (CanonicalFrame frame in frames)
            {
                _stream.Write(frame.Bytes.Span);
            }

            _stream.Write(marker);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The session log cannot continue after an I/O failure.");
        }
    }
}
