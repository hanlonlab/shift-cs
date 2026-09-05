using System.Net;
using Shift.Engine.EngineHost;
using Shift.Ipc;

if (args.Length != 1 || !Guid.TryParse(args[0], out Guid sessionId) || sessionId == Guid.Empty)
{
    Console.Error.WriteLine("Usage: Shift.Engine <session-id> (pair 1, engine producer 2)");
    return 1;
}

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
using UdpMulticastReceiver multicast = new(IPAddress.Parse("239.255.0.1"), 55_000, IPAddress.Loopback);
using UnixDatagramSender submissions = new("/run/shift/sequencer.in.sock");
EngineServer server = new(new CommittedSessionReader(multicast, sessionId), submissions, 1, 2);
try
{
    await server.RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
