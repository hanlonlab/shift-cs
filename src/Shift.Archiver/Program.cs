using Shift.Archiver;
using Shift.Ipc;

using var listener = UnixStreamSocket.Listen("/run/shift/archiver.sock");
using ArchiverServer archiver = new(
    "/var/lib/shift/archive",
    await listener.AcceptAsync());
await archiver.RunAsync();
