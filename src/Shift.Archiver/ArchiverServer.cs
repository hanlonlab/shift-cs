using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Control;

namespace Shift.Archiver;

public sealed class ArchiverServer(string archiveRoot) : IDisposable
{
    private const int MaximumBatchBytes = 1024 * 1024;
    private const int MaximumBatchFrameCount = MaximumBatchBytes / FrameCodec.MinimumFrameSize;

    private readonly SessionArchive _archive = new(archiveRoot);

    public async Task RunAsync(
        UnixStreamSocket sequencer,
        CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[sizeof(uint)];

        while (true)
        {
            CanonicalFrame[] frames = await ReceiveBatchAsync(
                sequencer,
                prefix,
                cancellationToken);
            long committedThrough = _archive.CommitBatch(frames);
            CanonicalFrame acknowledgement = CommitThroughCodec.Encode(
                frames[^1].Header.SessionId,
                committedThrough);
            await sequencer.SendExactlyAsync(acknowledgement.Bytes, cancellationToken);
        }
    }

    private static async Task<CanonicalFrame[]> ReceiveBatchAsync(
        UnixStreamSocket sequencer,
        byte[] prefix,
        CancellationToken cancellationToken)
    {
        await sequencer.ReceiveExactlyAsync(prefix, cancellationToken);
        uint frameCount = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (frameCount is 0 or > MaximumBatchFrameCount)
        {
            throw new InvalidDataException(
                $"A batch must contain between 1 and {MaximumBatchFrameCount} frames.");
        }

        var frames = new CanonicalFrame[frameCount];
        int batchBytes = 0;
        for (int index = 0; index < frames.Length; index++)
        {
            await sequencer.ReceiveExactlyAsync(prefix, cancellationToken);
            int frameLength = FrameCodec.ReadFrameLength(prefix);
            if (frameLength > MaximumBatchBytes - batchBytes)
            {
                throw new InvalidDataException(
                    $"A batch cannot exceed {MaximumBatchBytes} canonical frame bytes.");
            }

            byte[] frame = new byte[frameLength];
            prefix.CopyTo(frame, 0);
            await sequencer.ReceiveExactlyAsync(frame.AsMemory(sizeof(uint)), cancellationToken);
            frames[index] = FrameCodec.DecodeSequencedCandidate(frame);
            batchBytes += frameLength;
        }

        return frames;
    }

    public void Dispose()
    {
        _archive.Dispose();
    }
}
