using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.Site.Services.Contests;

public sealed record ParticipantImportSummary(int Added, int Existing);

/// <summary>Callers must authorize each participant operation before invoking it.</summary>
/// <remarks>Use a new scope when retrying a failed operation.</remarks>
public sealed class CompetitorManagementService(OlympContext context, UserManager<User> users,
    IStringLocalizer<SharedResource> localizer)
{
    public static string GetParticipantName(OlympUser user) =>
        $"{user.FirstName} {user.LastName}, {user.Organization?.Name}" + (user.Details is null ? "" : $", {user.Details}");

    public async Task RegisterIndividualAsync(int contestId, int userId, bool outOfCompetition, CancellationToken token = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token)
            ?? throw new EntityNotFoundException();

        if (contest.IsHidden || contest.IsAcm || DateTimeOffset.Now > contest.EndTime)
            throw new OperationValidationException(localizer["Registration is not available for this contest."]);

        var user = await context.OlympUsers.AsTracking().Include(x => x.Organization)
            .SingleOrDefaultAsync(x => x.Id == userId, token) ?? throw new EntityNotFoundException();

        if (await context.Competitors.AnyAsync(x => x.ContestId == contestId && x.Members.Any(m => m.Id == userId), token))
            throw new OperationValidationException(localizer["You are already registered for this contest!"]);
        if (user.FirstName is null || user.LastName is null || user.Organization?.Name is null)
            throw new OperationValidationException(localizer["Registration information is incorrect."]);
        if (user.Details is null && !outOfCompetition)
            throw new OperationValidationException(localizer["The {0} field in the profile must be filled in.", localizer["Group"]]);

        var participant = new Competitor
        {
            Name = GetParticipantName(user),
            Contest = null!,
            ContestId = contestId,
            IsHidden = user.IsHidden,
            IsOutOfCompetition = outOfCompetition,
            OrganizationId = user.OrganizationId,
            RegistrationDate = DateTimeOffset.Now,
            Members = [user],
            Messages = [],
            Submissions = []
        };

        var result = await users.CreateAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);

        await transaction.CommitAsync(token);
    }

    public async Task RegisterTeamAsync(int contestId, int organizationId, int coachId,
        string name, IReadOnlyList<string> participants, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || participants.Count != 3
            || participants.Any(x => string.IsNullOrWhiteSpace(x) || x.Contains(';')))
            throw new OperationValidationException(localizer["Registration information is incorrect."]);

        var participantIds = participants.Select(x => int.TryParse(x, out var id) ? (int?)id : null).ToArray();
        var memberIds = participantIds.OfType<int>().ToArray();
        if (memberIds.Distinct().Count() != memberIds.Length || participants.Distinct().Count() != participants.Count)
            throw new OperationValidationException(localizer["Participants must be different."]);

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token)
            ?? throw new EntityNotFoundException();

        if (contest.IsHidden || !contest.IsAcm || DateTimeOffset.Now > contest.EndTime)
            throw new OperationValidationException(localizer["Registration is not available for this contest."]);
        if (!await context.Organizations.AnyAsync(x => x.Id == organizationId, token)
            || !await context.OlympUsers.AnyAsync(x => x.Id == coachId, token))
            throw new EntityNotFoundException();

        var membersById = await context.OlympUsers.AsTracking()
            .Where(x => memberIds.Contains(x.Id) && x.OrganizationId == organizationId && !x.IsHidden)
            .ToDictionaryAsync(x => x.Id, token);

        if (membersById.Count != memberIds.Length)
            throw new OperationValidationException(localizer["Invalid value"]);
        if (await context.Competitors.AnyAsync(x => x.ContestId == contestId && x.Members.Any(m => memberIds.Contains(m.Id)), token))
            throw new OperationValidationException(localizer["One or more participants are already registered for this contest."]);

        var memberInfos = string.Join(";", participants.Select((text, i) => participantIds[i] is int id
            ? $"{membersById[id].LastName} {membersById[id].FirstName};{id}"
            : $"{text};"));

        var participant = new Competitor
        {
            Name = name,
            Contest = null!,
            ContestId = contestId,
            OrganizationId = organizationId,
            CoachId = coachId,
            MemberInfos = memberInfos,
            RegistrationDate = DateTimeOffset.Now,
            Members = [.. membersById.Values],
            Messages = [],
            Submissions = []
        };

        var result = await users.CreateAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);

        await transaction.CommitAsync(token);
    }

    public Task SetApprovalAsync(int contestId, int participantId, bool approved,
        CancellationToken token = default) =>
        UpdateAsync(contestId, participantId, participant => participant.IsApproved = approved, token);

    public Task SetOutOfCompetitionAsync(int contestId, int participantId, bool outOfCompetition,
        CancellationToken token = default) =>
        UpdateAsync(contestId, participantId, participant => participant.IsOutOfCompetition = outOfCompetition, token);

    public Task SetDisqualificationAsync(int contestId, int participantId, bool disqualified,
        CancellationToken token = default) =>
        UpdateAsync(contestId, participantId, participant => participant.IsDisqualified = disqualified, token);

    private async Task UpdateAsync(int contestId, int participantId,
        Action<Competitor> update, CancellationToken token)
    {
        var participant = await context.Competitors.AsTracking()
            .SingleOrDefaultAsync(x => x.Id == participantId && x.ContestId == contestId, token)
            ?? throw new EntityNotFoundException();

        update(participant);

        var result = await users.UpdateAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);
    }

    public async Task DeleteAsync(int contestId, int participantId,
        CancellationToken token = default)
    {
        var participant = await context.Competitors.AsTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == participantId && x.ContestId == contestId, token)
            ?? throw new EntityNotFoundException();

        if (await context.Submissions.AnyAsync(x => x.UserId == participantId, token)
            || await context.Messages.AnyAsync(x => x.UserId == participantId, token))
            throw new OperationValidationException(localizer["The participant cannot be deleted because submissions or other related records exist."]);

        participant.Members.Clear();

        var result = await users.DeleteAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);
    }

    public async Task<int> DeleteWithoutSubmissionsAsync(int contestId, CancellationToken token = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token)
            ?? throw new EntityNotFoundException();

        if (DateTimeOffset.Now <= contest.EndTime)
            throw new OperationValidationException(localizer["Participants without submissions can only be deleted after the contest has ended."]);

        var participantIds = await context.Competitors
            .Where(x => x.ContestId == contestId && !x.Submissions.Any() && !x.Messages.Any())
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(token);

        foreach (var participantId in participantIds)
        {
            token.ThrowIfCancellationRequested();

            await DeleteAsync(contestId, participantId, token);
        }

        await transaction.CommitAsync(token);

        return participantIds.Count;
    }

    public async Task ApproveAllAsync(int contestId, CancellationToken token = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.AsTracking().Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        foreach (var participant in contest.Competitors.Where(x => !x.IsApproved))
        {
            token.ThrowIfCancellationRequested();

            participant.IsApproved = true;
            var result = await users.UpdateAsync(participant);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);
        }

        await transaction.CommitAsync(token);
    }

    public async Task<ParticipantImportSummary> ImportAsync(int contestId,
        IReadOnlyCollection<string> names, CancellationToken token = default)
    {
        if (names.Count == 0)
            throw new OperationValidationException(localizer["The file contains no participant names."]);
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new OperationValidationException(localizer["The file contains duplicate participant names."]);

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        if (contest.IsAcm)
            throw new OperationValidationException(localizer["Participant names can only be imported into individual contests."]);

        var existing = contest.Competitors.ToLookup(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();

            var count = existing[name].Count();
            if (count > 1)
                throw new OperationValidationException(localizer["The contest contains multiple participants named '{0}'.", name]);
            if (count == 1)
                continue;

            var participant = new Competitor
            {
                ContestId = contestId,
                Contest = null!,
                Name = name,
                IsApproved = true,
                RegistrationDate = DateTimeOffset.Now,
                Members = [],
                Messages = [],
                Submissions = []
            };

            var result = await users.CreateAsync(participant);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);

            added++;
        }

        await transaction.CommitAsync(token);

        return new ParticipantImportSummary(added, names.Count - added);
    }

    public async Task AssignLoginsAsync(int contestId, IReadOnlyDictionary<string, string> logins,
        CancellationToken token = default)
    {
        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (login, password) in logins)
        {
            if (!available.TryAdd(login, password))
                throw new OperationValidationException(localizer["The file contains duplicate logins."]);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.AsTracking().Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        foreach (var participant in contest.Competitors)
            if (participant.UserName is { } username)
                available.Remove(username);

        if (available.Count < contest.Competitors.Count(x => x.UserName is null))
            throw new OperationValidationException(localizer["The file contains too few logins."]);

        foreach (var participant in contest.Competitors.Where(x => x.UserName is null)
            .OrderBy(x => x.RegistrationDate).ThenBy(x => x.Id))
        {
            token.ThrowIfCancellationRequested();

            var (login, password) = available.First();
            available.Remove(login);

            var result = await users.SetUserNameAsync(participant, login);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);

            result = await users.AddPasswordAsync(participant, password);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);

            result = await users.AddClaimAsync(participant, new Claim(ClaimNames.CompetitorContest, contestId.ToString()));
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);
        }

        await transaction.CommitAsync(token);
    }

    public async Task GenerateLoginsAsync(int contestId, CancellationToken token = default)
    {
        int count = await context.Competitors.CountAsync(x => x.ContestId == contestId, token);
        var logins = new Dictionary<string, string>();
        for (int i = 1; i <= count; ++i)
            logins.Add($"olymp{i:000}", RandomNumberGenerator.GetString("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", 10));

        await AssignLoginsAsync(contestId, logins, token);
    }

    public async Task RemoveLoginsAsync(int contestId, CancellationToken token = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.AsTracking().Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        foreach (var participant in contest.Competitors)
        {
            token.ThrowIfCancellationRequested();

            var result = await users.SetUserNameAsync(participant, null);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);

            result = await users.RemovePasswordAsync(participant);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);

            // Identity's claim store queries use the context's default tracking behavior.
            var trackingBehavior = context.ChangeTracker.QueryTrackingBehavior;
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
            try
            {
                result = await users.RemoveClaimAsync(participant, new Claim(ClaimNames.CompetitorContest, contestId.ToString()));
            }
            finally
            {
                context.ChangeTracker.QueryTrackingBehavior = trackingBehavior;
            }

            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);
        }

        await transaction.CommitAsync(token);
    }
}
