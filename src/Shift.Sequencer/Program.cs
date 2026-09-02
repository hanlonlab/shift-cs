using System.Net;
using Shift.Ipc;
using Shift.Sequencer;

using UnixDatagramReceiver submissions = new("/run/shift/sequencer.in.sock");
using UnixStreamSocket archiver = await UnixStreamSocket.ConnectAsync(
    "/run/shift/archiver.sock");
using UdpMulticastSender multicast = new(
    IPAddress.Parse("239.255.0.1"),
    55_000,
    IPAddress.Loopback);

SequencerServer server = new(submissions, archiver, multicast);
await server.RunAsync();
