using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Shift.Protocol.Internal.Control;

namespace Shift.Engine.EngineHost;

/// <summary>
/// Reads contiguous, watermark-confirmed multicast batches for one session.
/// The caller owns the receiver and must use a single reader at a time.
/// A missing or invalid frame stops this reader; recovery is outside this live path.
/// </summary>
public sealed class CommittedSessionReader
{
    private const int MaximumPendingBytes = 1_048_576;

    private readonly UdpMulticastReceiver _receiver;
    private readonly byte[] _receiveBuffer = new byte[FrameCodec.MaximumFrameSize];
    private readonly List<CanonicalFrame> _pending = [];
    private int _pendingBytes;
    private bool _faulted;

    public CommittedSessionReader(UdpMulticastReceiver receiver, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        _receiver = receiver;
        SessionId = sessionId;
    }

    public Guid SessionId { get; }

    public long LastCommittedSequence { get; private set; }

    public async Task<IReadOnlyList<CanonicalFrame>> ReadBatchAsync(
        CancellationToken cancellationToken = default)
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The committed session reader stopped after a protocol error.");
        }

        try
        {
            while (true)
            {
                int length = await _receiver.ReceiveAsync(_receiveBuffer, cancellationToken);
                byte[] bytes = _receiveBuffer.AsSpan(0, length).ToArray();
                CanonicalFrame frame = FrameCodec.Decode(bytes);
                FrameHeader header = frame.Header;

                if (header.SessionId != SessionId)
                {
                    continue;
                }

                if (header.MessageType == MessageType.CommitThrough)
                {
                    CommitThroughCodec.Validate(frame);
                    long committedSequence = header.SequenceId;
                    if (committedSequence <= LastCommittedSequence)
                    {
                        continue;
                    }

                    if (_pending.Count == 0 || committedSequence != _pending[^1].Header.SequenceId)
                    {
                        throw new InvalidDataException("Commit watermark does not match the contiguous pending batch.");
                    }

                    CanonicalFrame[] batch = _pending.ToArray();
                    LastCommittedSequence = committedSequence;
                    _pending.Clear();
                    _pendingBytes = 0;
                    return batch;
                }

                FrameCodec.ValidateSequencedCandidate(frame);
                long sequence = frame.Header.SequenceId;
                if (sequence <= LastCommittedSequence)
                {
                    continue;
                }

                long pendingIndex = sequence - LastCommittedSequence - 1;
                if (pendingIndex < _pending.Count)
                {
                    if (!frame.Bytes.Span.SequenceEqual(_pending[(int)pendingIndex].Bytes.Span))
                    {
                        throw new InvalidDataException("A pending sequence was repeated with different bytes.");
                    }

                    continue;
                }

                if (pendingIndex != _pending.Count)
                {
                    throw new InvalidDataException("Multicast sequence contains a gap.");
                }

                if (sequence == 1
                    && (frame.Header.MessageType != MessageType.StartNewSession
                        || !StartNewSessionCodec.TryDecode(frame.Payload.Span, out _)))
                {
                    throw new InvalidDataException("The first committed frame must start the session.");
                }

                if (frame.Bytes.Length > MaximumPendingBytes - _pendingBytes)
                {
                    throw new InvalidDataException("The pending multicast batch exceeded one MiB.");
                }

                _pending.Add(frame);
                _pendingBytes += frame.Bytes.Length;
            }
        }
        catch (InvalidDataException)
        {
            _faulted = true;
            throw;
        }
    }
}
