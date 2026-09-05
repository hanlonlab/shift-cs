using System.Net;
using System.Net.Sockets;
using Shift.Archiver;
using Shift.Engine.EngineHost;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.Protocol.Internal.Events;
using Shift.Sequencer;

namespace Shift.LoadGenerator;

public sealed record MatchingSmokeResult(
    Guid SessionId,
    string ArchivePath,
    IReadOnlyList<CanonicalFrame> CommittedFrames,
    long EngineAppliedThrough,
    int EngineObservedResults);

/// <summary>Exercises the real IPC and durable journal with one quote and one participant IOC.</summary>
public static class MatchingSmokeScenario
{
    public static async Task<MatchingSmokeResult> RunAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        string archiveRoot = Path.Combine(directory, "archive");
        string submissionPath = Path.Combine(directory, "in.sock");
        Directory.CreateDirectory(archiveRoot);
        var sessionId = Guid.NewGuid();
        var group = IPAddress.Parse("239.255.43.10");
        int port = GetUnusedPort();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(15));

        // Both subscribers join before the first submission; this live slice has no gap recovery.
        using UdpMulticastReceiver observerSocket = new(group, port, IPAddress.Loopback);
        using UdpMulticastReceiver engineSocket = new(group, port, IPAddress.Loopback);
        using UnixDatagramReceiver ingress = new(submissionPath);
        using SessionArchive archive = new(archiveRoot);
        using UdpMulticastSender multicast = new(group, port, IPAddress.Loopback);
        using UnixDatagramSender participant = new(submissionPath);
        using UnixDatagramSender engineSubmissions = new(submissionPath);
        CommittedSessionReader observer = new(observerSocket, sessionId);
        EngineServer engine = new(new CommittedSessionReader(engineSocket, sessionId), engineSubmissions, 1, 2);
        SequencerServer sequencer = new(ingress, archive, multicast);
        Task sequencerTask = sequencer.RunAsync(shutdown.Token);
        Task engineTask = engine.RunAsync(shutdown.Token);
        Task<IReadOnlyList<CanonicalFrame>> scenarioTask = ExchangeAsync(
            participant, observer, sessionId, shutdown.Token);
        try
        {
            // Surface a failed service immediately rather than waiting for the scenario timeout.
            Task first = await Task.WhenAny(scenarioTask, sequencerTask, engineTask);
            await first;
            IReadOnlyList<CanonicalFrame> frames = await scenarioTask;
            await engineTask;
            return new MatchingSmokeResult(
                sessionId,
                Path.Combine(archiveRoot, $"{sessionId:N}.shiftlog"),
                frames,
                engine.AppliedThrough,
                engine.ObservedResultCount);
        }
        finally
        {
            await shutdown.CancelAsync();
            try
            {
                await Task.WhenAll(sequencerTask, engineTask, scenarioTask);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<IReadOnlyList<CanonicalFrame>> ExchangeAsync(
        UnixDatagramSender submissions,
        CommittedSessionReader observer,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        List<CanonicalFrame> frames = [];
        await SubmitAsync(MessageType.StartNewSession, 1, ReadOnlyMemory<byte>.Empty);
        await ObserveThroughAsync(1);

        byte[] payload = new byte[40];
        int length = UpdateReferenceQuoteCodec.Encode(
            new UpdateReferenceQuote(1, new ReferenceLevel(99, 10), new ReferenceLevel(100, 10)), payload);
        await SubmitAsync(MessageType.UpdateReferenceQuote, 2, payload.AsMemory(0, length));
        await ObserveThroughAsync(2);

        length = PlaceOrderCodec.Encode(
            new PlaceOrder(1, 1, OrderSide.Buy, 101, 4, OrderType.ImmediateOrCancelLimit), payload);
        await SubmitAsync(MessageType.PlaceOrder, 3, payload.AsMemory(0, length));
        await ObserveThroughAsync(5);

        if (frames[3].Header.MessageType != MessageType.OrderUpdated
            || !OrderUpdatedCodec.TryDecode(frames[3].Payload.Span, out OrderUpdated update)
            || update != new OrderUpdated(1, 1, 0, 0, RejectionReason.None, CancellationReason.None)
            || frames[4].Header.MessageType != MessageType.TradeExecuted
            || !TradeExecutedCodec.TryDecode(frames[4].Payload.Span, out TradeExecuted execution)
            || execution != new TradeExecuted(1, new Fill(1, 100, 4, FillRole.Taker)))
        {
            throw new InvalidDataException("The committed IOC outcome did not match the reference quote.");
        }

        await SubmitAsync(MessageType.EndCurrentSession, 4, ReadOnlyMemory<byte>.Empty);
        await ObserveThroughAsync(6);
        return frames;

        async ValueTask SubmitAsync(MessageType type, ulong producerSequence, ReadOnlyMemory<byte> body)
        {
            CanonicalFrame frame = FrameCodec.Encode(type, sessionId, 1, producerSequence, 0, body.Span);
            await submissions.SendAsync(frame.Bytes, cancellationToken);
        }

        async Task ObserveThroughAsync(long sequence)
        {
            while (observer.LastCommittedSequence < sequence)
            {
                frames.AddRange(await observer.ReadBatchAsync(cancellationToken));
            }

            if (observer.LastCommittedSequence != sequence)
            {
                throw new InvalidDataException("The smoke scenario observed unexpected messages.");
            }
        }
    }

    private static int GetUnusedPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
