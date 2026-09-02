using System.Buffers;
using System.Buffers.Binary;
using Shift.Ipc;
using Shift.Protocol.Framing;

namespace Shift.Sequencer;

public sealed class SequencerServer
{
    private static readonly TimeSpan _maximumBatchDelay = TimeSpan.FromMilliseconds(1);

    private readonly SequencerState _state = new();
    private readonly UnixDatagramReceiver _submissions;
    private readonly UnixStreamSocket _archiver;
    private readonly UdpMulticastSender _multicast;
    private readonly List<ReadOnlyMemory<byte>> _pendingBatch = [];
    private int _batchBytes;
    private CancellationTokenSource? _batchDeadline;
    private readonly byte[] _receiveBuffer = new byte[UnixDatagramReceiver.MaximumDatagramSize];
    private byte[]? _committedThroughFrame;

    public SequencerServer(
        UnixDatagramReceiver submissions,
        UnixStreamSocket archiver,
        UdpMulticastSender multicast)
    {
        _submissions = submissions;
        _archiver = archiver;
        _multicast = multicast;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                int? frameLength = await ReceiveSubmissionAsync(cancellationToken);
                if (frameLength is null)
                {
                    await ArchiveAndPublishBatchAsync(cancellationToken);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (_batchDeadline?.IsCancellationRequested == true)
                {
                    await ArchiveAndPublishBatchAsync(cancellationToken);
                }

                await HandleSubmissionAsync(frameLength.Value, cancellationToken);
            }
        }
        finally
        {
            _batchDeadline?.Dispose();
        }
    }

    private async ValueTask<int?> ReceiveSubmissionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _submissions.ReceiveAsync(
                _receiveBuffer,
                _batchDeadline?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && _pendingBatch.Count != 0)
        {
            return null;
        }
    }

    private async Task HandleSubmissionAsync(
        int frameLength,
        CancellationToken cancellationToken)
    {
        var verified = VerifiedSubmission.Verify(
            _receiveBuffer.AsMemory(0, frameLength));
        SubmissionResult submission = _state.Submit(verified);
        if (submission.Status == SubmissionStatus.BatchFull)
        {
            await ArchiveAndPublishBatchAsync(cancellationToken);
            submission = _state.Submit(verified);
            if (submission.Status == SubmissionStatus.BatchFull)
            {
                throw new InvalidDataException("A submission cannot fit in an empty batch.");
            }
        }

        if (submission.Status == SubmissionStatus.PendingDuplicate)
        {
            return;
        }

        if (submission.Status == SubmissionStatus.CommittedDuplicate)
        {
            if (_committedThroughFrame is null)
            {
                throw new InvalidDataException("A committed submission has no durable watermark.");
            }

            await _multicast.SendAsync(submission.Frame, cancellationToken);
            await _multicast.SendAsync(_committedThroughFrame, cancellationToken);
            return;
        }

        _pendingBatch.Add(submission.Frame);
        _batchBytes += submission.Frame.Length;

        if (_pendingBatch.Count == 1)
        {
            _batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _batchDeadline.CancelAfter(_maximumBatchDelay);
        }

        if (submission.ForceCommit)
        {
            await ArchiveAndPublishBatchAsync(cancellationToken);
        }
    }

    private async Task ArchiveAndPublishBatchAsync(CancellationToken cancellationToken)
    {
        byte[] request = new byte[sizeof(uint) + _batchBytes];
        BinaryPrimitives.WriteUInt32BigEndian(request, checked((uint)_pendingBatch.Count));

        int offset = sizeof(uint);
        foreach (ReadOnlyMemory<byte> frame in _pendingBatch)
        {
            frame.Span.CopyTo(request.AsSpan(offset));
            offset += frame.Length;
        }

        await _archiver.SendExactlyAsync(request, cancellationToken);

        byte[] lengthPrefix = new byte[sizeof(uint)];
        await _archiver.ReceiveExactlyAsync(lengthPrefix, cancellationToken);
        uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
        if (encodedLength is < FrameCodec.MinimumFrameSize
            or > UnixDatagramReceiver.MaximumDatagramSize)
        {
            throw new InvalidDataException("The Archiver returned an invalid durable watermark length.");
        }

        byte[] durableWatermark = new byte[encodedLength];
        lengthPrefix.CopyTo(durableWatermark, 0);
        await _archiver.ReceiveExactlyAsync(durableWatermark.AsMemory(sizeof(uint)), cancellationToken);

        OperationStatus status = FrameCodec.TryDecode(
            durableWatermark,
            out FrameHeader header,
            out ReadOnlySpan<byte> payload);
        long expectedSequence = _state.LastAcceptedSequence;
        if (status != OperationStatus.Done
            || header.MessageType != MessageType.CommitThrough
            || header.MessageId != Guid.Empty
            || header.SequenceId != expectedSequence
            || !payload.IsEmpty)
        {
            throw new InvalidDataException("The Archiver returned an invalid durable watermark.");
        }

        _state.CommitThrough(expectedSequence);
        foreach (ReadOnlyMemory<byte> frame in _pendingBatch)
        {
            await _multicast.SendAsync(frame, cancellationToken);
        }

        await _multicast.SendAsync(durableWatermark, cancellationToken);
        _committedThroughFrame = durableWatermark;
        _pendingBatch.Clear();
        _batchBytes = 0;
        _batchDeadline!.Dispose();
        _batchDeadline = null;
    }
}
