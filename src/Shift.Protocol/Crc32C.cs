using System.Buffers.Binary;
using System.Numerics;

namespace Shift.Protocol;

public static class Crc32C
{
    public static uint Compute(ReadOnlySpan<byte> source)
    {
        uint checksum = uint.MaxValue;
        while (source.Length >= sizeof(ulong))
        {
            checksum = BitOperations.Crc32C(checksum, BinaryPrimitives.ReadUInt64LittleEndian(source));
            source = source[sizeof(ulong)..];
        }

        foreach (byte value in source)
        {
            checksum = BitOperations.Crc32C(checksum, value);
        }

        return ~checksum;
    }
}
