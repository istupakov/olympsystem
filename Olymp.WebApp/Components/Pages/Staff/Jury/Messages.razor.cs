using System.ComponentModel.DataAnnotations;
using System.Data.Common;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Pages.Staff.Jury;

public partial class Messages
{
    [Parameter]
    public int ContestId { get; set; }
    [SupplyParameterFromQuery]
    public bool Announcements { get; set; }
    [SupplyParameterFromQuery]
    public int? Problem { get; set; }
    [SupplyParameterFromQuery]
    public string? Author { get; set; }
    [SupplyParameterFromQuery]
    public bool? Answered { get; set; }
    [SupplyParameterFromQuery]
    public int? Page { get; set; }
    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;

    private readonly CancellationTokenSource _stop = new();
    private Task? _poll;
    private IJSObjectReference? _sound;
    private int? _soundId;
    private ElementReference _soundButton;
    private ElementReference _soundStatus;
    private Contest? _contest;
    private List<Problem> _problems = [];
    private List<Message> _messages = [];
    private HashSet<int>? _knownQuestions;
    private int _observedContest;
    private Filters _filters = new();
    private Editor _editor = new();
    private Message? _pending;
    private int? _editingId;
    private int? _originalProblem;
    private string? _originalText;
    private string? _question;
    private string? _error;
    private bool _loaded;
    private bool _canManage;
    private bool _busy;
    private bool _refreshing;
    private bool _editing;
    private bool _isReply;
    private bool _preview;
    private bool _hasNext;
    private bool _soundInitialized;
    private int CurrentPage => Math.Max(1, Page ?? 1);

    protected override async Task OnParametersSetAsync()
    {
        if (_observedContest != ContestId)
        {
            _knownQuestions = null;
            _observedContest = ContestId;
            _editing = false;
            _pending = null;
        }
        _filters = new() { Author = Author, Problem = Problem, Answered = Answered };
        await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
        _contest = await db.Contests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ContestId, _stop.Token);
        _problems = await db.Problems.AsNoTracking().Where(item => item.ContestId == ContestId)
            .OrderBy(item => item.Number).ToListAsync(_stop.Token);
        await RefreshAsync();
        _loaded = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_soundInitialized && _contest is not null)
        {
            _soundInitialized = true;
            try
            {
                _sound = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Staff/Jury/Messages.razor.js");
                _soundId = await _sound.InvokeAsync<int>("initialize", _soundButton, _soundStatus, "sounds/new-message.wav");
            }
            catch (JSDisconnectedException)
            {
                return;
            }
            catch (JSException exception)
            {
                Logger.LogWarning(exception, "Could not enable message sound");
            }
            _poll ??= PollAsync();
        }
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await InvokeAsync(async () =>
                {
                    if (_stop.IsCancellationRequested)
                        return;
                    await RefreshAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _contest is null)
            return;
        _refreshing = true;
        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
            _canManage = (await Authorization.AuthorizeAsync((await AuthenticationState).User, ContestId, PolicyNames.ManageContest)).Succeeded;
            var questions = await db.Messages.Where(item => item.ContestId == ContestId && item.UserId != null)
                .Select(item => item.Id).ToHashSetAsync(_stop.Token);
            bool notify = _knownQuestions is not null && questions.Except(_knownQuestions).Any();
            _knownQuestions = questions;
            var query = db.Messages.AsNoTracking().Where(item => item.ContestId == ContestId
                && (Announcements ? item.UserId == null : item.UserId != null));
            if (Problem is int problem)
                query = query.Where(item => item.ProblemId == problem);
            if (!Announcements && !string.IsNullOrWhiteSpace(Author))
                query = query.Where(item => item.User != null && (item.User.Name.Contains(Author)
                    || (item.User.UserName != null && item.User.UserName.Contains(Author))));
            if (!Announcements && Answered is bool answered)
                query = query.Where(item => answered ? item.JuryText != null && item.JuryText.Trim() != ""
                    : item.JuryText == null || item.JuryText.Trim() == "");
            long skip = ((long)CurrentPage - 1) * 20;
            _messages = skip <= int.MaxValue ? await query.Include(item => item.User).Include(item => item.Problem)
                .OrderByDescending(item => item.SendTime).ThenByDescending(item => item.Id)
                .Skip((int)skip).Take(21).ToListAsync(_stop.Token) : [];
            _hasNext = _messages.Count > 20;
            if (_hasNext)
                _messages.RemoveAt(20);
            if (notify && _sound is not null && _soundId is not null)
                await _sound.InvokeVoidAsync("notify", _soundId);
        }
        catch (Exception exception) when (exception is DbException or JSDisconnectedException)
        {
            Logger.LogWarning(exception, "Could not refresh messages for contest {ContestId}", ContestId);
            _error = SiteResources.StaffRefreshFailed;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void NewAnnouncement()
    {
        _editingId = null;
        _editor = new() { ProblemId = Problem };
        _question = _originalText = null;
        _originalProblem = null;
        _editing = true;
        _isReply = _preview = false;
        _error = null;
    }

    private void Edit(Message message)
    {
        _editingId = message.Id;
        _originalText = message.JuryText;
        _originalProblem = message.ProblemId;
        _editor = new() { Text = message.JuryText ?? "", ProblemId = message.ProblemId };
        _question = message.UserText;
        _isReply = message.UserId is not null;
        _editing = true;
        _preview = false;
        _error = null;
    }

    private async Task SaveAsync()
    {
        if (_busy || !_editing)
            return;
        _busy = true;
        _error = null;
        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
            if (!(await Authorization.AuthorizeAsync((await AuthenticationState).User, ContestId, PolicyNames.ManageContest)).Succeeded)
            {
                _error = SiteResources.StaffAccessDenied;
                return;
            }
            if (_editor.ProblemId is int problem && !await db.Problems.AnyAsync(item => item.Id == problem && item.ContestId == ContestId, _stop.Token))
            {
                _error = SiteResources.PageNotFoundMessage;
                return;
            }
            if (_editingId is int id)
            {
                int changed = await db.Messages.Where(item => item.Id == id && item.ContestId == ContestId
                    && item.UserId != null == _isReply && item.JuryText == _originalText && item.ProblemId == _originalProblem)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.JuryText, _editor.Text)
                        .SetProperty(item => item.ProblemId, _editor.ProblemId), _stop.Token);
                if (changed == 0)
                {
                    _error = SiteResources.StaffMessageConflict;
                    return;
                }
            }
            else
            {
                db.Messages.Add(new Message
                {
                    Contest = null!,
                    ContestId = ContestId,
                    ProblemId = _editor.ProblemId,
                    JuryText = _editor.Text,
                    SendTime = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync(_stop.Token);
            }
            _editing = false;
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            Logger.LogError(exception, "Could not save a message for contest {ContestId}", ContestId);
            _error = SiteResources.StaffSaveFailed;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_busy || _pending is not { } pending)
            return;
        _busy = true;
        _error = null;
        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync(_stop.Token);
            if (!(await Authorization.AuthorizeAsync((await AuthenticationState).User, ContestId, PolicyNames.ManageContest)).Succeeded)
            {
                _error = SiteResources.StaffAccessDenied;
                return;
            }
            var query = db.Messages.Where(item => item.Id == pending.Id && item.ContestId == ContestId
                && item.UserId == pending.UserId && item.JuryText == pending.JuryText && item.ProblemId == pending.ProblemId);
            int changed = pending.UserId is null ? await query.ExecuteDeleteAsync(_stop.Token)
                : await query.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.JuryText, (string?)null), _stop.Token);
            if (changed == 0)
                _error = SiteResources.StaffMessageConflict;
            else if (_editingId == pending.Id)
                _editing = false;
            _pending = null;
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            Logger.LogError(exception, "Could not delete message {MessageId}", pending.Id);
            _error = SiteResources.StaffSaveFailed;
        }
        finally
        {
            _busy = false;
        }
    }

    private string TabUrl(bool announcements) => $"jury/contests/{ContestId}/messages" + (announcements ? "?Announcements=true" : "");
    private string PageUrl(int page) => Navigation.GetUriWithQueryParameter(nameof(Page), page);
    private void ApplyFilters() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        [nameof(Problem)] = _filters.Problem,
        [nameof(Author)] = _filters.Author,
        [nameof(Answered)] = _filters.Answered,
        [nameof(Page)] = null
    }));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_sound is not null)
        {
            try
            {
                if (_soundId is not null)
                    await _sound.InvokeVoidAsync("dispose", _soundId);
                await _sound.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
        if (_poll is not null)
            await _poll;
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Filters
    {
        public int? Problem { get; set; }
        public string? Author { get; set; }
        public bool? Answered { get; set; }
    }

    private sealed class Editor
    {
        [Required(ErrorMessageResourceType = typeof(SiteResources), ErrorMessageResourceName = nameof(SiteResources.StaffMessageRequired))]
        public string Text { get; set; } = "";
        public int? ProblemId { get; set; }
    }
}
