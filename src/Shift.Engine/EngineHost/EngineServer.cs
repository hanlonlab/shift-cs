using Shift.Engine.Matching;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;

namespace Shift.Engine.EngineHost;

/// <summary>
/// Runs one instrument and participant for one live session. Only reference quotes and
/// IOC orders are supported in this slice. The caller owns the sockets and cancellation.
/// </summary>
public sealed class EngineServer
{
    private readonly CommittedSessionReader _committed;
    private readonly EngineResultPublisher _results;
    private readonly MatchingEngine _engine;
    private readonly Fill[] _fills = new Fill[1];

    public EngineServer(
        CommittedSessionReader committed,
        UnixDatagramSender submissions,
        long pairId,
        ushort producerId)
    {
        _committed = committed;
        _results = new EngineResultPublisher(submissions, committed.SessionId, producerId);
        _engine = new MatchingEngine(pairId);
    }

    public long AppliedThrough { get; private set; }

    public int ObservedResultCount => _results.ObservedCount;

    public bool IsSessionActive => _engine.IsSessionActive;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            IReadOnlyList<CanonicalFrame> batch = await _committed.ReadBatchAsync(cancellationToken);
            foreach (CanonicalFrame frame in batch)
            {
                bool ended = await ApplyAsync(frame, cancellationToken);
                AppliedThrough = frame.Header.SequenceId;
                if (ended)
                {
                    if (AppliedThrough != _committed.LastCommittedSequence)
                    {
                        throw new InvalidDataException("Messages followed the session end.");
                    }

                    return;
                }
            }
        }
    }

    private async ValueTask<bool> ApplyAsync(CanonicalFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Header.MessageType is MessageType.OrderUpdated or MessageType.TradeExecuted)
        {
            _results.Observe(frame);
            return false;
        }

        if (frame.Header.ProducerId == _results.ProducerId)
        {
            throw new InvalidDataException("The engine producer ID must be reserved for engine results.");
        }

        switch (frame.Header.MessageType)
        {
            case MessageType.StartNewSession:
                if (!StartNewSessionCodec.TryDecode(frame.Payload.Span, out StartNewSession start)
                    || _engine.StartSession(start) != StartSessionStatus.Started)
                {
                    throw new InvalidDataException("Invalid engine session start.");
                }

                break;
            case MessageType.UpdateReferenceQuote:
                if (!UpdateReferenceQuoteCodec.TryDecode(frame.Payload.Span, out UpdateReferenceQuote quote)
                    || _engine.UpdateReferenceQuote(quote.PairId, quote.Bid, quote.Ask) != RejectionReason.None)
                {
                    throw new InvalidDataException("Invalid reference quote for this engine session.");
                }

                break;
            case MessageType.PlaceOrder:
                if (!PlaceOrderCodec.TryDecode(frame.Payload.Span, out PlaceOrder order))
                {
                    throw new InvalidDataException("Invalid order payload.");
                }

                OrderResult result = order.OrderType == OrderType.ImmediateOrCancelLimit
                    ? _engine.Place(order, _fills)
                    : new OrderResult(RejectionReason.UnsupportedOrderType);
                await _results.PublishAsync(order, result, _fills.AsMemory(0, result.FillCount), cancellationToken);
                break;
            case MessageType.EndCurrentSession:
                if (_results.HasPending)
                {
                    throw new InvalidDataException("Engine results must be observed committed before ending the session.");
                }

                if (!EndCurrentSessionCodec.TryDecode(frame.Payload.Span, out EndCurrentSession end)
                    || _engine.EndSession(end, [], out _) != RejectionReason.None)
                {
                    throw new InvalidDataException("Invalid engine session end.");
                }

                return true;
            default:
                throw new InvalidDataException("This engine host supports reference quotes and IOC orders only.");
        }

        return false;
    }
}
