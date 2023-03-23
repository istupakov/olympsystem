using System;
using System.Diagnostics;

namespace Olymp.QReply.Loggers;

public class TraceLogWriter : LogWriterBase
{
    #region Поля
    private static TraceLogWriter? s_instance;
    private static readonly object SyncRoot = new();
    private TraceSource? _source;
    #endregion Поля
    #region Свойства
    public TraceSource? Source
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

    public static TraceLogWriter GetInstance()
    {
        lock (SyncRoot)
        {
            s_instance ??= new TraceLogWriter
            {
                Source = new TraceSource("MyApplication", SourceLevels.All)
            };
            return s_instance;
        }
    }

    public override void WriteEntry(string message, LogEntryType entryType, Exception? exception)
    {
        lock (SyncRoot)
        {
            if (_source == null)
            {
                return;
            }
            try
            {
                var eventType = GetTraceEntryType(entryType);

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
                _source.TraceEvent(eventType, 0, message);
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

    private TraceLogWriter()
    {
    }

    private TraceEventType GetTraceEntryType(LogEntryType entryType)
    {
        return entryType switch
        {
            LogEntryType.Error => TraceEventType.Error,
            LogEntryType.Information => TraceEventType.Information,
            LogEntryType.Warning => TraceEventType.Warning,
            _ => TraceEventType.Information,
        };
    }

    #endregion private Методы.
    #endregion Методы
}
