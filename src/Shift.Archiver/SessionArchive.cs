using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;

namespace Shift.Archiver;

internal sealed class SessionArchive(string archiveRoot) : IDisposable
{
    private SessionLog? _sessionLog;
    private long _highWater;

    public long CommitBatch(ReadOnlySpan<CanonicalFrame> frames)
    {
        bool sessionActive = _sessionLog is not null;
        long highWater = _highWater;
        Guid sessionId = Guid.Empty;
        bool endsSession = false;

        for (int index = 0; index < frames.Length; index++)
        {
            CanonicalFrame frame = frames[index];
            long expectedSequence = checked(highWater + 1);
            if (frame.Header.SequenceId != expectedSequence)
            {
                throw new InvalidDataException(
                    $"Expected sequence {expectedSequence}, received {frame.Header.SequenceId}.");
            }

            if (frame.Header.MessageType == MessageType.StartNewSession)
            {
                if (sessionActive)
                {
                    throw new InvalidDataException("A session is already active.");
                }

                if (!StartNewSessionCodec.TryDecode(frame.Payload.Span, out StartNewSession command))
                {
                    throw new InvalidDataException(
                        "An inactive Archiver batch must begin with a valid StartNewSession.");
                }

                sessionId = command.SessionId;
                sessionActive = true;
            }
            else if (!sessionActive)
            {
                throw new InvalidDataException(
                    "An inactive Archiver batch must begin with StartNewSession.");
            }

            if (frame.Header.MessageType == MessageType.EndCurrentSession)
            {
                if (!EndCurrentSessionCodec.IsValidPayload(frame.Payload.Span))
                {
                    throw new InvalidDataException("EndCurrentSession payload must be empty.");
                }

                if (index != frames.Length - 1)
                {
                    throw new InvalidDataException(
                        "EndCurrentSession must be the final frame in its batch.");
                }

                endsSession = true;
            }

            highWater = frame.Header.SequenceId;
        }

        if (_sessionLog is null)
        {
            string path = Path.Combine(archiveRoot, $"{sessionId:N}.shiftlog");
            _sessionLog = new SessionLog(path);
        }

        _sessionLog.CommitBatch(frames, highWater);
        _highWater = highWater;

        if (endsSession)
        {
            _sessionLog.Dispose();
            _sessionLog = null;
            _highWater = 0;
        }

        return highWater;
    }

    public void Dispose()
    {
        _sessionLog?.Dispose();
    }
}
