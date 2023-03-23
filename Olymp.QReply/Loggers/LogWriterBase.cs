using System;

namespace Olymp.QReply.Loggers;

public abstract class LogWriterBase : ILogWriter
{
    public virtual void WriteEntry(string message)
    {
        WriteEntry(message, LogEntryType.Information);
    }

    public virtual void WriteEntry(string message, LogEntryType entryType)
    {
        WriteEntry(message, entryType, null);
    }

    public abstract void WriteEntry(string message, LogEntryType entryType, Exception? exception);

    public virtual bool TryToWriteEntry(string message)
    {
        return TryToWriteEntry(message, LogEntryType.Information);
    }

    public virtual bool TryToWriteEntry(string message, LogEntryType entryType)
    {
        return TryToWriteEntry(message, entryType, null);
    }

    public virtual bool TryToWriteEntry(string message, LogEntryType entryType, Exception? exception)
    {
        try
        {
            WriteEntry(message, entryType, exception);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
