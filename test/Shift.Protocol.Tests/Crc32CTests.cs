using System.Numerics;
using Xunit;

namespace Shift.Protocol.Tests;

public class Crc32CTests
{
    [Fact]
    public void ComputeReturnsCanonicalValue()
    {
        byte[] source = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39];

        Assert.Equal(0xe3069283u, Crc32C.Compute(source));
    }

    [Fact]
    public void ComputeMatchesByteAtATimeForEveryRemainder()
    {
        byte[] source = new byte[65];
        new Random(42).NextBytes(source);

        for (int length = 0; length < source.Length; length++)
        {
            ReadOnlySpan<byte> data = source.AsSpan(1, length);
            uint checksum = uint.MaxValue;
            foreach (byte value in data)
            {
                checksum = BitOperations.Crc32C(checksum, value);
            }

            Assert.Equal(~checksum, Crc32C.Compute(data));
        }
    }
}
