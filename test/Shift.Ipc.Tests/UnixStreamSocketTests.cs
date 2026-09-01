using System.Buffers;
using System.Buffers.Binary;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Ipc.Tests;

public sealed class UnixStreamSocketTests
{
    [Fact]
    public async Task SendsCountAndCanonicalFrames()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "AF_UNIX test requires macOS or Linux.");

        string directory = Path.Combine("/tmp", $"shift-{Guid.NewGuid():N}");
        string socketPath = Path.Combine(directory, "stream.sock");
        Directory.CreateDirectory(directory);

        try
        {
            byte[] payload = new byte[16];
            StartNewSessionCodec.Encode(new StartNewSession(Guid.NewGuid()), payload);

            byte[] firstFrame = new byte[FrameCodec.MinimumFrameSize + payload.Length];
            FrameCodec.Encode(MessageType.StartNewSession, Guid.NewGuid(), 1, payload, firstFrame);

            byte[] secondFrame = new byte[FrameCodec.MinimumFrameSize + payload.Length];
            FrameCodec.Encode(MessageType.StartNewSession, Guid.NewGuid(), 2, payload, secondFrame);

            byte[] batch = new byte[sizeof(uint) + firstFrame.Length + secondFrame.Length];
            BinaryPrimitives.WriteUInt32BigEndian(batch, 2);
            firstFrame.CopyTo(batch.AsSpan(sizeof(uint)));
            secondFrame.CopyTo(batch.AsSpan(sizeof(uint) + firstFrame.Length));

            using var listener = UnixStreamSocket.Listen(socketPath);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<UnixStreamSocket> acceptTask = listener.AcceptAsync(timeout.Token).AsTask();
            using UnixStreamSocket client = await UnixStreamSocket.ConnectAsync(socketPath, timeout.Token);
            using UnixStreamSocket server = await acceptTask;

            await client.SendExactlyAsync(batch, timeout.Token);

            byte[] countBytes = new byte[sizeof(uint)];
            await server.ReceiveExactlyAsync(countBytes, timeout.Token);
            uint frameCount = BinaryPrimitives.ReadUInt32BigEndian(countBytes);
            Assert.Equal(2U, frameCount);

            byte[][] expectedFrames = [firstFrame, secondFrame];
            for (int index = 0; index < frameCount; index++)
            {
                byte[] lengthBytes = new byte[sizeof(uint)];
                await server.ReceiveExactlyAsync(lengthBytes, timeout.Token);
                int frameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes));

                byte[] frame = new byte[frameLength];
                lengthBytes.CopyTo(frame, 0);
                await server.ReceiveExactlyAsync(frame.AsMemory(sizeof(uint)), timeout.Token);

                Assert.Equal(expectedFrames[index], frame);
                Assert.Equal(
                    OperationStatus.Done,
                    FrameCodec.TryDecode(frame, out FrameHeader header, out _));
                Assert.Equal(index + 1, header.SequenceId);
            }

            using CancellationTokenSource canceled = new();
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await server.ReceiveExactlyAsync(new byte[1], canceled.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
