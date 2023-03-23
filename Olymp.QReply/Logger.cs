using System;

using Caliburn.Micro;

using Olymp.QReply.Loggers;

namespace Olymp.QReply;

internal class Logger : ILog
{
    public static ILog Default { get; } = new Logger();

    static Logger()
    {
        var lw = WindowsLogWriter.GetInstance();
        lw.Source = lw.Log = "OlympQReply";
    }

    public void Error(Exception exception)
    {
        WindowsLogWriter.GetInstance().TryToWriteEntry("Необработанное исключение", LogEntryType.Error, exception);
    }

    public void Info(string format, params object[] args)
    {
        WindowsLogWriter.GetInstance().TryToWriteEntry(string.Format(format, args), LogEntryType.Information);
    }

    public void Warn(string format, params object[] args)
    {
        WindowsLogWriter.GetInstance().TryToWriteEntry(string.Format(format, args), LogEntryType.Warning);
    }
}
