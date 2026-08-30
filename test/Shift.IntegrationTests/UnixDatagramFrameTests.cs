using System.Buffers;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.IntegrationTests;

public class UnixDatagramFrameTests
{
    [Fact]
    public async Task SendsAndReceivesEncodedFrame()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX integration test requires macOS or Linux.");

        string directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        string socketPath = Path.Combine(directory, "in.sock");
        Directory.CreateDirectory(directory);

        try
        {
            StartNewSession command = new(
                new Guid("00112233-4455-6677-8899-aabbccddeeff"));
            Guid messageId = new("10213243-5465-7687-98a9-bacbdcedfe0f");
            byte[] encodedPayload = new byte[16];
            StartNewSessionCodec.Encode(command, encodedPayload);

            byte[] encodedFrame = new byte[FrameCodec.MinimumFrameSize + encodedPayload.Length];
            int frameLength = FrameCodec.Encode(
                MessageType.StartNewSession,
                messageId,
                sequenceId: 0,
                encodedPayload,
                encodedFrame);

            Assert.Equal(51, frameLength);

            using UnixDatagramReceiver receiver = new(socketPath);
            using UnixDatagramSender sender = new(socketPath);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

            await sender.SendAsync(encodedFrame, timeout.Token);

            byte[] receivedFrame = new byte[UnixDatagramReceiver.MaximumDatagramSize];
            int bytesReceived = await receiver.ReceiveAsync(receivedFrame, timeout.Token);

            Assert.Equal(frameLength, bytesReceived);
            Assert.Equal(encodedFrame, receivedFrame[..bytesReceived]);
            Assert.Equal(
                OperationStatus.Done,
                FrameCodec.TryDecode(
                    receivedFrame.AsSpan(0, bytesReceived),
                    out FrameHeader header,
                    out ReadOnlySpan<byte> receivedPayload));
            Assert.Equal((uint)frameLength, header.FrameLength);
            Assert.Equal(FrameCodec.CurrentVersion, header.Version);
            Assert.Equal(MessageType.StartNewSession, header.MessageType);
            Assert.Equal(messageId, header.MessageId);
            Assert.Equal(0, header.SequenceId);
            Assert.True(StartNewSessionCodec.TryDecode(receivedPayload, out StartNewSession receivedCommand));
            Assert.Equal(command, receivedCommand);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
