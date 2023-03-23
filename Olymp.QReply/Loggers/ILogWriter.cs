using System;

namespace Olymp.QReply.Loggers;

public interface ILogWriter
{
    bool TryToWriteEntry(string message);
    bool TryToWriteEntry(string message, LogEntryType entryType);
    bool TryToWriteEntry(string message, LogEntryType entryType, Exception exception);
    void WriteEntry(string message);
    void WriteEntry(string message, LogEntryType entryType);
    void WriteEntry(string message, LogEntryType entryType, Exception exception);
}
