using Shift.Archiver;
using Shift.Ipc;

using var listener = UnixStreamSocket.Listen("/run/shift/archiver.sock");
using UnixStreamSocket sequencer = await listener.AcceptAsync();
using ArchiverServer archiver = new("/var/lib/shift/archive");
await archiver.RunAsync(sequencer);
