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
}
