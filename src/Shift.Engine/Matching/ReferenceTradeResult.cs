using Shift.Protocol.Internal;

namespace Shift.Engine.Matching;

public readonly record struct ReferenceTradeResult(
    RejectionReason RejectionReason,
    int FillCount = 0,
    long UnallocatedQuantity = 0);
