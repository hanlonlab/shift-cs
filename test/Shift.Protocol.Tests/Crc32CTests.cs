using System.Numerics;
using Xunit;

namespace Shift.Protocol.Tests;

public class Crc32CTests
{
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
