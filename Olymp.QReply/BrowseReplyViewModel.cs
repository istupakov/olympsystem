using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows.Data;
using System.Windows.Threading;

using Caliburn.Micro;

using Olymp.Domain.Models;
using Olymp.QReply.Windows;
using Olymp.QReply.Windows.Converters;

using Message = Olymp.Domain.Models.Message;

namespace Olymp.QReply;

internal class BrowseReplyViewModel : PropertyChangedBase
{
    #region Fields
    private readonly DispatcherTimer _refreshTimer;
    private OlympDataContext? _context;
    private Contest? _selectedContest;
    private bool _isShowWithAnswersEnabled = true;
    private bool _isNewJuryCommentGlobal = true;
    private Message? _selectedMessage;
    private Problem? _selectedProblem;
    private string? _selectedMessageAnswer;
    private string? _selectedDefaultAnswer;
    private string? _newJuryCommentText;
    private BindableCollection<Message>? _messages;
    private BindableCollection<Message>? _juryComments;
    private BindableCollection<Problem>? _problems;
    private readonly BindableCollection<string> _defaultAnswers = new(
        new[] { "Да", "Нет", "Без комментариев", "Yes", "No", "No comments" });
    private bool _newMessageTrigger;
    private bool _answerSentTrigger;
    private bool _juryCommentSentTrigger;
    #endregion

    #region Properties

    public BindableCollection<Message>? Messages
    {
        get => _messages;
        private set { _messages = value; NotifyOfPropertyChange(() => Messages); }
    }

    public bool AnswerSentTrigger
    {
        get => _answerSentTrigger;
        private set { _answerSentTrigger = value; NotifyOfPropertyChange(() => AnswerSentTrigger); }
    }

    public bool JuryCommentSentTrigger
    {
        get => _juryCommentSentTrigger;
        private set { _juryCommentSentTrigger = value; NotifyOfPropertyChange(() => JuryCommentSentTrigger); }
    }

    public string? SelectedMessageAnswer
    {
        get => _selectedMessageAnswer;
        set { _selectedMessageAnswer = value; NotifyOfPropertyChange(() => SelectedMessageAnswer); }
    }

    public string? NewJuryCommentText
    {
        get => _newJuryCommentText;
        set { _newJuryCommentText = value; NotifyOfPropertyChange(() => NewJuryCommentText); }
    }

    public bool NewMessageTrigger
    {
        get => _newMessageTrigger;
        private set { _newMessageTrigger = value; NotifyOfPropertyChange(() => NewMessageTrigger); }
    }

    public Message? SelectedMessage
    {
        get => _selectedMessage;
        set { _selectedMessage = value; NotifyOfPropertyChange(() => SelectedMessage); }
    }

    public Problem? SelectedProblem
    {
        get => _selectedProblem;
        set { _selectedProblem = value; NotifyOfPropertyChange(() => SelectedProblem); }
    }

    public string? SelectedDefaultAnswer
    {
        get => _selectedDefaultAnswer;
        set { _selectedDefaultAnswer = value; NotifyOfPropertyChange(() => SelectedDefaultAnswer); }
    }

    public bool IsShowWithAnswersEnabled
    {
        get => _isShowWithAnswersEnabled;
        set { _isShowWithAnswersEnabled = value; NotifyOfPropertyChange(() => IsShowWithAnswersEnabled); }
    }

    public bool IsNewJuryCommentGlobal
    {
        get => _isNewJuryCommentGlobal;
        set { _isNewJuryCommentGlobal = value; NotifyOfPropertyChange(() => IsNewJuryCommentGlobal); }
    }

    public Contest? SelectedContest
    {
        get => _selectedContest;
        set { _selectedContest = value; NotifyOfPropertyChange(() => SelectedContest); }
    }

    public ObservableCollection<Contest> Contests => _context.Contests;

    public BindableCollection<Message>? JuryComments
    {
        get => _juryComments;
        set { _juryComments = value; NotifyOfPropertyChange(() => JuryComments); }
    }

    public BindableCollection<Problem>? Problems
    {
        get => _problems;
        set { _problems = value; NotifyOfPropertyChange(() => Problems); }
    }

    public BindableCollection<string> DefaultAnswers => _defaultAnswers;
    #endregion

    #region Methods
    public BrowseReplyViewModel()
    {
        InitDataContext();
        SetPropertiesDependencies();
        _refreshTimer = new DispatcherTimer();
#pragma warning disable CS8622
        _refreshTimer.Tick += new EventHandler(RefreshTimer_Tick);
#pragma warning restore CS8622
        _refreshTimer.Interval = TimeSpan.FromSeconds(10);
        _refreshTimer.Start();
    }

    public void SaveAnswer()
    {
        if (SelectedMessage == null)
            return;
        if (SelectedMessage.JuryText == SelectedMessageAnswer)
            return;

        try
        {
            SelectedMessage.JuryText = SelectedMessageAnswer;
            _context.Save();
            RaiseAnswerSent();
            NotifyOfPropertyChange(() => SelectedMessage);
        }
        catch (DataException ex)
        {
            LogManager.GetLog(GetType()).Error(ex);
            IoC.Get<IMessageBoxService>().ShowMessage("Не удалось сохранить ответ (возможно, проблема доступа к БД). \nПриложение будет закрыто");
            IoC.Get<IAppService>().Shutdown();
        }
    }

    public void SaveJuryComment()
    {
        if (string.IsNullOrWhiteSpace(NewJuryCommentText))
            return;
        if (SelectedContest == null)
            return;

        try
        {
            var msg = _context.CreateMessage();
            msg.JuryText = NewJuryCommentText;
            msg.Contest = SelectedContest;
            msg.Problem = SelectedProblem;
            msg.User = null;
            msg.UserText = null;
            msg.SendTime = _context.GetCurrentDateTime();
            _context.InsertMessage(msg);
            _context.Save();
            RaiseJuryCommentSent();
            NewJuryCommentText = string.Empty;
        }
        catch (DataException ex)
        {
            LogManager.GetLog(GetType()).Error(ex);
            IoC.Get<IMessageBoxService>().ShowMessage("Не удалось сохранить комментарий (возможно, проблема доступа к БД). \nПриложение будет закрыто");
            IoC.Get<IAppService>().Shutdown();
        }
    }

    public void AddDefaultAnswerToMsg()
    {
        if (!string.IsNullOrWhiteSpace(SelectedDefaultAnswer))
            SelectedMessageAnswer += SelectedDefaultAnswer;
    }

    private void RefreshTimer_Tick(object sender, EventArgs e)
    {
        RefreshSelectedContest();
    }

    private void TuneMessagesView()
    {
        if (Messages == null)
            return;

        var cv = CollectionViewSource.GetDefaultView(Messages);
        if (cv.SortDescriptions.Count == 0)
            cv.SortDescriptions.Add(new SortDescription("SendTime", ListSortDirection.Descending));
        cv.Filter = (obj) =>
        {
            if (obj is not Message msg)
                return false;
            return IsShowWithAnswersEnabled || (bool)IsStringNullOrWhiteSpaceConverter.ConvertValue(msg.JuryText);
        };
    }

    private void RefreshSelectedContest()
    {
        var noAnswerMsgQty = SelectedContestNoAnswerMsgs();

        try
        {
            _context.RefreshContestMessages(SelectedContest);
        }
        catch (DataException ex)
        {
            LogManager.GetLog(GetType()).Error(ex);
            IoC.Get<IMessageBoxService>().ShowMessage("Не удалось обновить данные (возможно, проблема доступа к БД). \nПриложение будет закрыто");
            IoC.Get<IAppService>().Shutdown();
        }

        NotifyOfPropertyChange(() => SelectedContest);

        if (noAnswerMsgQty < SelectedContestNoAnswerMsgs())
            RaiseNewMessage();
    }

    private void RaiseNewMessage()
    {
        NewMessageTrigger = !(NewMessageTrigger = true);
    }

    private void RaiseAnswerSent()
    {
        AnswerSentTrigger = !(AnswerSentTrigger = true);
    }

    private void RaiseJuryCommentSent()
    {
        JuryCommentSentTrigger = !(JuryCommentSentTrigger = true);
    }

    private int SelectedContestNoAnswerMsgs()
    {
        if (SelectedContest == null)
            return -1;
        if (SelectedContest.Messages == null)
            return 0;

        return SelectedContest.Messages.Count(m => m.User != null && (bool)IsStringNullOrWhiteSpaceConverter.ConvertValue(m.JuryText));
    }

    private void InitDataContext()
    {
        try
        {
            _context = IoC.Get<OlympDataContext>();
            _context.InitDataContext();
        }
        catch (DataException ex)
        {
            LogManager.GetLog(GetType()).Error(ex);
            IoC.Get<IMessageBoxService>().ShowMessage("Не удалось получить данные (проверьте настройки подключения). \nПриложение будет закрыто");
            IoC.Get<IAppService>().Shutdown();
        }
    }

    private void SetPropertiesDependencies()
    {
        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == "SelectedContest")
            {
                if (SelectedContest == null)
                {
                    Messages = null;
                    JuryComments = null;
                    Problems = null;
                }
                else
                {
                    if (SelectedContest.Messages == null)
                    {
                        Messages = null;
                        JuryComments = null;
                    }
                    else
                    {
                        Messages = new BindableCollection<Message>(SelectedContest.Messages.Where(msg => msg.User != null));
                        TuneMessagesView();
                        JuryComments = new BindableCollection<Message>(SelectedContest.Messages.Where(msg => msg.User == null));
                        if (SelectedContest.Problems == null) Problems = null;
                        else Problems = new BindableCollection<Problem>(SelectedContest.Problems);
                    }
                }

                if (SelectedProblem == null)
                    IsNewJuryCommentGlobal = true;
            }

            if (e.PropertyName == "IsShowWithAnswersEnabled")
            {
                TuneMessagesView();
            }

            if (e.PropertyName == "SelectedMessage")
            {
                if (SelectedMessage != null)
                {
                    SelectedMessageAnswer = SelectedMessage.JuryText;
                }
            }

            if (e.PropertyName == "IsNewJuryCommentGlobal")
            {
                if (IsNewJuryCommentGlobal)
                    SelectedProblem = null;
                else
                    SelectedProblem = Problems != null ? Problems.FirstOrDefault() : null;
            }
        };
    }

    #endregion
}
