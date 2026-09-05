using Shift.Protocol.Internal;

namespace Shift.Engine.Matching;

/// <summary>Latest recorded quote. Simulated consumption never changes these quantities.</summary>
internal sealed class ReferenceBook
{
    internal ReferenceLevel Bid { get; private set; }

    internal ReferenceLevel Ask { get; private set; }

    internal static RejectionReason Validate(ReferenceLevel bid, ReferenceLevel ask)
    {
        if (!IsValidLevel(bid) || !IsValidLevel(ask))
        {
            return RejectionReason.InvalidReferenceQuote;
        }

        if (bid.Quantity > 0 && ask.Quantity > 0 && bid.PriceTicks >= ask.PriceTicks)
        {
            return RejectionReason.LockedOrCrossedReferenceQuote;
        }

        return RejectionReason.None;
    }

    internal void Update(ReferenceLevel bid, ReferenceLevel ask)
    {
        Bid = bid;
        Ask = ask;
    }

    private static bool IsValidLevel(ReferenceLevel level)
    {
        return level == default || (level.PriceTicks > 0 && level.Quantity > 0);
    }
}
