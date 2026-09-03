using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Mathematics.OutlierDetection;
using Shift.Archiver;
using Shift.Ipc;
using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Shift.Sequencer;

IEnumerable<Summary> summaries = BenchmarkSwitcher
    .FromAssembly(typeof(CommittedMessageEndToEndBenchmarks).Assembly)
    .Run(args);

Environment.ExitCode = summaries.Any()
    && summaries.All(summary =>
        !summary.HasCriticalValidationErrors
        && summary.Reports.Any()
        && summary.Reports.All(report => report.Success))
    ? 0
    : 1;

[ArtifactsPath("bin/BenchmarkDotNet.Artifacts")]
[GcForce(false)]
[MinIterationCount(50)]
[MinWarmupCount(20)]
[Outliers(OutlierMode.DontRemove)]
public class CommittedMessageEndToEndBenchmarks
{
    private readonly byte[] _payload = new byte[
        UnixDatagramReceiver.MaximumDatagramSize - FrameCodec.MinimumFrameSize];
    private readonly byte[] _submission = new byte[UnixDatagramReceiver.MaximumDatagramSize];
    private readonly byte[] _lifecycleSubmission = new byte[FrameCodec.MinimumFrameSize + 16];
    private readonly byte[] _receiveBuffer = new byte[UnixDatagramReceiver.MaximumDatagramSize];

    private string _directory = null!;
    private CancellationTokenSource _shutdown = null!;
    private UnixStreamSocket _listener = null!;
    private UdpMulticastReceiver _committed = null!;
    private UnixDatagramReceiver _submissionReceiver = null!;
    private UnixStreamSocket _archiverConnection = null!;
    private UnixStreamSocket _archiverStream = null!;
    private UdpMulticastSender _multicast = null!;
    private ArchiverServer _archiver = null!;
    private UnixDatagramSender _submissions = null!;
    private Task _sequencerTask = null!;
    private Task _archiverTask = null!;
    private CancellationTokenSource _iterationCancellation = null!;
    private Task<int> _receiveTask = null!;
    private ushort _producerId;
    private ulong _producerSequence;
    private int _payloadLength;
    private int _submissionLength;
    private long _expectedSequenceId;
    private int _receivedLength;

    [Params(MessageType.StartNewSession, MessageType.EndCurrentSession)]
    public MessageType Message { get; set; }

    [GlobalSetup]
    public void StartServers()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The benchmark requires AF_UNIX sockets.");
        }

        _directory = Path.Combine("/tmp", $"shift-benchmark-{Guid.NewGuid():N}");
        string archiveRoot = Path.Combine(_directory, "archive");
        string submissionPath = Path.Combine(_directory, "in.sock");
        string archiverPath = Path.Combine(_directory, "archive.sock");
        var group = IPAddress.Parse("239.255.44.1");
        int port = GetUnusedPort();
        Directory.CreateDirectory(archiveRoot);

        _shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _listener = UnixStreamSocket.Listen(archiverPath);
        _committed = new UdpMulticastReceiver(group, port, IPAddress.Loopback);
        _submissionReceiver = new UnixDatagramReceiver(submissionPath);
        _archiverConnection = UnixStreamSocket.ConnectAsync(
            archiverPath,
            _shutdown.Token).AsTask().GetAwaiter().GetResult();
        _multicast = new UdpMulticastSender(group, port, IPAddress.Loopback);

        var sequencer = new SequencerServer(
            _submissionReceiver,
            _archiverConnection,
            _multicast);
        _sequencerTask = sequencer.RunAsync(_shutdown.Token);
        _archiverStream = _listener
            .AcceptAsync(_shutdown.Token).AsTask().GetAwaiter().GetResult();
        _archiver = new ArchiverServer(archiveRoot);
        _archiverTask = _archiver.RunAsync(_archiverStream, _shutdown.Token);
        _submissions = new UnixDatagramSender(submissionPath);
    }

    [IterationSetup]
    public void PrepareMessage()
    {
        _iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _receivedLength = 0;

        switch (Message)
        {
            case MessageType.StartNewSession:
                _payloadLength = StartNewSessionCodec.Encode(
                    new StartNewSession(Guid.NewGuid()),
                    _payload);
                break;
            case MessageType.EndCurrentSession:
                _payloadLength = 0;
                break;
            default:
                throw new NotSupportedException($"No end-to-end fixture exists for {Message}.");
        }

        if (Message != MessageType.StartNewSession)
        {
            StartSession();
        }

        _expectedSequenceId = Message == MessageType.StartNewSession ? 1 : 2;
        _producerId = 1;
        _producerSequence = Message == MessageType.StartNewSession ? 1uL : 2uL;
        _submissionLength = FrameCodec.Encode(
            Message,
            _producerId,
            _producerSequence,
            0,
            _payload.AsSpan(0, _payloadLength),
            _submission);

        _receiveTask = _committed.ReceiveAsync(
            _receiveBuffer,
            _iterationCancellation.Token).AsTask();
    }

    [Benchmark]
    public async Task SubmitToCommittedMulticast()
    {
        await _submissions.SendAsync(
            _submission.AsMemory(0, _submissionLength),
            _iterationCancellation.Token);
        _receivedLength = await _receiveTask;
    }

    [IterationCleanup]
    public void FinishMessage()
    {
        if (_receivedLength == 0)
        {
            _iterationCancellation.Cancel();
            try
            {
                _receiveTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            if (Message != MessageType.StartNewSession)
            {
                EndSession(2);
            }

            _iterationCancellation.Dispose();
            return;
        }

        ValidateFrame(
            _receivedLength,
            Message,
            _producerId,
            _producerSequence,
            _expectedSequenceId,
            _payload.AsSpan(0, _payloadLength));
        ReceiveAndValidateFrame(
            MessageType.CommitThrough,
            FrameCodec.ControlProducerId,
            0,
            _expectedSequenceId,
            ReadOnlySpan<byte>.Empty);

        if (Message != MessageType.EndCurrentSession)
        {
            EndSession(_expectedSequenceId + 1);
        }

        _iterationCancellation.Dispose();
    }

    [GlobalCleanup]
    public void StopServers()
    {
        _shutdown.Cancel();
        try
        {
            Task.WhenAll(_sequencerTask, _archiverTask).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _submissions.Dispose();
            _archiver.Dispose();
            _archiverStream.Dispose();
            _multicast.Dispose();
            _archiverConnection.Dispose();
            _submissionReceiver.Dispose();
            _committed.Dispose();
            _listener.Dispose();
            _shutdown.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void ReceiveAndValidateFrame(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        ReadOnlySpan<byte> expectedPayload)
    {
        int frameLength = _committed.ReceiveAsync(
            _receiveBuffer,
            _shutdown.Token).AsTask().GetAwaiter().GetResult();
        ValidateFrame(
            frameLength,
            messageType,
            producerId,
            producerSequence,
            sequenceId,
            expectedPayload);
    }

    private void StartSession()
    {
        Span<byte> payload = stackalloc byte[16];
        int payloadLength = StartNewSessionCodec.Encode(
            new StartNewSession(Guid.NewGuid()),
            payload);
        int frameLength = FrameCodec.Encode(
            MessageType.StartNewSession,
            1,
            1,
            0,
            payload[..payloadLength],
            _lifecycleSubmission);

        _submissions.SendAsync(
            _lifecycleSubmission.AsMemory(0, frameLength),
            _shutdown.Token).AsTask().GetAwaiter().GetResult();
        ReceiveAndValidateFrame(
            MessageType.StartNewSession,
            1,
            1,
            1,
            payload[..payloadLength]);
        ReceiveAndValidateFrame(
            MessageType.CommitThrough,
            FrameCodec.ControlProducerId,
            0,
            1,
            ReadOnlySpan<byte>.Empty);
    }

    private void EndSession(long sequenceId)
    {
        int frameLength = FrameCodec.Encode(
            MessageType.EndCurrentSession,
            1,
            2,
            0,
            ReadOnlySpan<byte>.Empty,
            _lifecycleSubmission);

        _submissions.SendAsync(
            _lifecycleSubmission.AsMemory(0, frameLength),
            _shutdown.Token).AsTask().GetAwaiter().GetResult();
        ReceiveAndValidateFrame(
            MessageType.EndCurrentSession,
            1,
            2,
            sequenceId,
            ReadOnlySpan<byte>.Empty);
        ReceiveAndValidateFrame(
            MessageType.CommitThrough,
            FrameCodec.ControlProducerId,
            0,
            sequenceId,
            ReadOnlySpan<byte>.Empty);
    }

    private void ValidateFrame(
        int frameLength,
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        ReadOnlySpan<byte> expectedPayload)
    {
        OperationStatus status = FrameCodec.TryDecode(
            _receiveBuffer.AsSpan(0, frameLength),
            out FrameHeader header,
            out ReadOnlySpan<byte> payload);
        if (status != OperationStatus.Done
            || header.MessageType != messageType
            || header.ProducerId != producerId
            || header.ProducerSequence != producerSequence
            || header.SequenceId != sequenceId
            || !payload.SequenceEqual(expectedPayload))
        {
            throw new InvalidDataException("Received an unexpected committed frame.");
        }
    }

    private static int GetUnusedPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
