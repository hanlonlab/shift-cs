using System.Net;
using Shift.Sequencer;

SequencerServer server = new(
    "/run/shift/sequencer.in.sock",
    "/run/shift/archiver.sock",
    IPAddress.Parse("239.255.0.1"),
    55_000,
    IPAddress.Loopback);

await server.RunAsync();
