using Shift.Archiver;
using Shift.Ipc;

using ArchiverServer archiver = new("/var/lib/shift/archive");
using var listener = UnixStreamSocket.Listen("/run/shift/archiver.sock");
using UnixStreamSocket sequencer = await listener.AcceptAsync();
await archiver.RunAsync(sequencer);
