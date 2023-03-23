using System;
using System.Diagnostics;

namespace Olymp.QReply.Loggers;

public class WindowsLogWriter : LogWriterBase
{
    #region Поля		
    private string _log = "MyJournal";
    private string _source = "MySource";
    private static WindowsLogWriter? s_instance;
    private static readonly object SyncRoot = new();
    #endregion Поля
    #region Свойства

    public string Log
    {
        get
        {
            lock (SyncRoot)
            {
                return _log;
            }
        }
        set
        {
            lock (SyncRoot)
            {
                _log = value;
            }
        }
    }

    public string Source
    {
        get
        {
            lock (SyncRoot)
            {
                return _source;
            }
        }
        set
        {
            lock (SyncRoot)
            {
                _source = value;
            }
        }
    }

    #endregion Свойства
    #region Методы
    #region public Методы.

    public static WindowsLogWriter GetInstance()
    {
        lock (SyncRoot)
        {
            s_instance ??= new WindowsLogWriter();
            return s_instance;
        }
    }

    public override void WriteEntry(string message, LogEntryType entryType, Exception? exception)
    {
        lock (SyncRoot)
        {
            try
            {
                EventLog eventLog = new(Log, ".", Source);
                EventLogEntryType eventLogEntryType = GetEventLogEntryType(entryType);

                string infoFromEx = string.Empty;
                Exception? ex = exception;
                string tabs = string.Empty;
                while (ex != null)
                {
                    //tabs += "\t";
                    infoFromEx += string.Format(
                        "\n\n{0}СООБЩЕНИЕ: {1}\n{0}СТЕК ВЫЗОВОВ: {2}",
                        tabs, ex.Message, ex.StackTrace);
                    ex = ex.InnerException;
                }
                message += infoFromEx;
                eventLog.WriteEntry(message, eventLogEntryType);
            }
            catch (Exception ex)
            {
                throw new ApplicationException(
                    GetType().Name + ".WriteEntry: " +
                    "не удалось добавить запись в журнал",
                    ex);
            }
        }
    }

    #endregion public Методы.
    #region private Методы.

    private WindowsLogWriter()
    {
    }

    private EventLogEntryType GetEventLogEntryType(LogEntryType entryType)
    {
        return entryType switch
        {
            LogEntryType.Error => EventLogEntryType.Error,
            LogEntryType.Information => EventLogEntryType.Information,
            LogEntryType.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
    }

    #endregion private Методы.
    #endregion Методы
}
