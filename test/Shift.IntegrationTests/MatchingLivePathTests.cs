using System.Buffers.Binary;
using Shift.LoadGenerator;
using Shift.Protocol;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Events;
using Xunit;

namespace Shift.IntegrationTests;

public sealed class MatchingLivePathTests
{
    [Fact]
    public async Task QuoteAndIocProduceCommittedResultsBeforeSessionEnds()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX integration test requires macOS or Linux.");
        string directory = Path.Combine("/tmp", $"shift-match-{Guid.NewGuid():N}");
        try
        {
            MatchingSmokeResult result = await MatchingSmokeScenario.RunAsync(directory, TestContext.Current.CancellationToken);
            Assert.Equal(new[]
            {
                MessageType.StartNewSession,
                MessageType.UpdateReferenceQuote,
                MessageType.PlaceOrder,
                MessageType.OrderUpdated,
                MessageType.TradeExecuted,
                MessageType.EndCurrentSession,
            }, result.CommittedFrames.Select(frame => frame.Header.MessageType));
            Assert.Equal(Enumerable.Range(1, 6).Select(sequence => (long)sequence),
                result.CommittedFrames.Select(frame => frame.Header.SequenceId));
            Assert.All(result.CommittedFrames, frame => Assert.Equal(result.SessionId, frame.Header.SessionId));
            Assert.Equal(new ushort[] { 1, 1, 1, 2, 2, 1 }, result.CommittedFrames.Select(frame => frame.Header.ProducerId));
            Assert.Equal(new ulong[] { 1, 2, 3, 1, 2, 4 }, result.CommittedFrames.Select(frame => frame.Header.ProducerSequence));
            Assert.Equal(6, result.EngineAppliedThrough);
            Assert.Equal(2, result.EngineObservedResults);
            Assert.True(OrderUpdatedCodec.TryDecode(result.CommittedFrames[3].Payload.Span, out OrderUpdated update));
            Assert.Equal(new OrderUpdated(1, 1, 0, 0, RejectionReason.None, CancellationReason.None), update);
            Assert.True(TradeExecutedCodec.TryDecode(result.CommittedFrames[4].Payload.Span, out TradeExecuted execution));
            Assert.Equal(new TradeExecuted(1, new Fill(1, 100, 4, FillRole.Taker)), execution);
            AssertArchive(result);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void AssertArchive(MatchingSmokeResult result)
    {
        byte[] bytes = File.ReadAllBytes(result.ArchivePath);
        int offset = 0;
        int frameCount = 0;
        long committedThrough = 0;
        while (offset < bytes.Length)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) == 0)
            {
                ReadOnlySpan<byte> marker = bytes.AsSpan(offset, 16);
                Assert.Equal(Crc32C.Compute(marker[..12]), BinaryPrimitives.ReadUInt32BigEndian(marker[12..]));
                committedThrough = BinaryPrimitives.ReadInt64BigEndian(marker[4..]);
                Assert.Equal(frameCount, committedThrough);
                offset += marker.Length;
            }
            else
            {
                int length = FrameCodec.ReadFrameLength(bytes.AsSpan(offset));
                Assert.Equal(result.CommittedFrames[frameCount++].Bytes.ToArray(), bytes.AsSpan(offset, length).ToArray());
                offset += length;
            }
        }

        Assert.Equal(6, frameCount);
        Assert.Equal(6, committedThrough);
    }
}
