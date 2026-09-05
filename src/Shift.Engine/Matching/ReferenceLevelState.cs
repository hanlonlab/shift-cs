namespace Shift.Engine.Matching;

public readonly record struct ReferenceLevelState(
    long PriceTicks,
    long RawObservedQuantity,
    long ExecutableShadowQuantity);
