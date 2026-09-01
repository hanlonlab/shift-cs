using System.Numerics;

namespace Shift.Protocol;

public static class Crc32C
{
    public static uint Compute(ReadOnlySpan<byte> source)
    {
        uint checksum = uint.MaxValue;
        foreach (byte value in source)
        {
            checksum = BitOperations.Crc32C(checksum, value);
        }

        return ~checksum;
    }
}
