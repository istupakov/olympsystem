using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed record ParticipantImportSummary(int Added, int Existing);

public sealed record TeamMember(int? UserId, string? Name);

/// <summary>Authorizes staff operations and registration cancellation. Callers authorize new registrations.</summary>
/// <remarks>Use a new scope when retrying a failed operation.</remarks>
public sealed class CompetitorManagementService(OlympContext context, UserManager<User> users,
    IAuthorizationService authorization, TimeProvider timeProvider)
{
    private async Task AuthorizeAsync(ClaimsPrincipal actor, int contestId)
    {
        if (!(await authorization.AuthorizeAsync(actor, contestId, PolicyNames.ManageContest)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
    }

    public static string GetParticipantName(OlympUser user) =>
        $"{user.FirstName} {user.LastName}, {user.Organization?.Name}" + (user.Details is null ? "" : $", {user.Details}");

    public async Task RegisterIndividualAsync(int contestId, int userId, bool outOfCompetition, CancellationToken token = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token)
            ?? throw new EntityNotFoundException();

        if (contest.IsHidden || contest.IsAcm || timeProvider.GetLocalNow() > contest.EndTime)
            throw new OperationValidationException(SiteResources.RegistrationNotAvailable);

        var user = await context.OlympUsers.AsTracking().Include(x => x.Organization)
            .SingleOrDefaultAsync(x => x.Id == userId, token) ?? throw new EntityNotFoundException();

        if (await context.Competitors.AnyAsync(x => x.ContestId == contestId && x.Members.Any(m => m.Id == userId), token))
            throw new OperationValidationException(SiteResources.AlreadyRegistered);
        if (user.FirstName is null || user.LastName is null || user.Organization?.Name is null)
            throw new OperationValidationException(SiteResources.RegistrationInformationIncorrect);
        if (user.Details is null && !outOfCompetition)
            throw new OperationValidationException(SiteResources.ProfileGroupRequired);

        var participant = new Competitor
        {
            Name = GetParticipantName(user),
            Contest = null!,
            ContestId = contestId,
            IsHidden = user.IsHidden,
            IsOutOfCompetition = outOfCompetition,
            OrganizationId = user.OrganizationId,
            RegistrationDate = timeProvider.GetLocalNow(),
            Members = [user],
            Messages = [],
            Submissions = []
        };

        var result = await users.CreateAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);

        await transaction.CommitAsync(token);
    }

    public static string? GetTeamMemberName(OlympUser user) =>
        string.IsNullOrWhiteSpace(user.LastName) || string.IsNullOrWhiteSpace(user.FirstName)
            ? null : $"{user.LastName} {user.FirstName}";

    public Task RegisterTeamAsync(int contestId, int organizationId, int coachId,
        string name, IReadOnlyList<string> participants, CancellationToken token = default)
        => RegisterTeamAsync(contestId, organizationId, coachId, name,
            [.. participants.Select(value => int.TryParse(value, out var id) ? new TeamMember(id, null) : new TeamMember(null, value))],
            false, token);

    public async Task RegisterTeamAsync(int contestId, int organizationId, int coachId,
        string name, IReadOnlyList<TeamMember> participants, bool outOfCompetition, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || participants.Count is < 1 or > 3
            || (!outOfCompetition && participants.Count != 3)
            || participants.Any(member => member.Name?.Contains(';') == true
                || (member.UserId is null && string.IsNullOrWhiteSpace(member.Name))))
            throw new OperationValidationException(SiteResources.RegistrationInformationIncorrect);

        var memberIds = participants.Where(member => member.UserId.HasValue).Select(member => member.UserId!.Value).ToArray();
        if (memberIds.Distinct().Count() != memberIds.Length)
            throw new OperationValidationException(SiteResources.ParticipantsMustBeDifferent);

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token)
            ?? throw new EntityNotFoundException();

        if (contest.IsHidden || !contest.IsAcm || timeProvider.GetLocalNow() > contest.EndTime)
            throw new OperationValidationException(SiteResources.RegistrationNotAvailable);
        if (contest.IsIcpc && !outOfCompetition)
            throw new OperationValidationException(SiteResources.IcpcRegistrationUnofficialOnly);
        if (!await context.Organizations.AnyAsync(x => x.Id == organizationId, token)
            || !await context.OlympUsers.AnyAsync(x => x.Id == coachId, token))
            throw new EntityNotFoundException();

        var membersById = await context.OlympUsers.AsTracking()
            .Where(x => memberIds.Contains(x.Id) && x.OrganizationId == organizationId && !x.IsHidden)
            .ToDictionaryAsync(x => x.Id, token);

        if (membersById.Count != memberIds.Length)
            throw new OperationValidationException(SiteResources.InvalidParticipantValue);
        if (await context.Competitors.AnyAsync(x => x.ContestId == contestId && x.Members.Any(m => memberIds.Contains(m.Id)), token))
            throw new OperationValidationException(SiteResources.ParticipantsAlreadyRegistered);

        if (await context.Competitors.AnyAsync(x => x.ContestId == contestId && x.Name == name, token))
            throw new OperationValidationException(SiteResources.TeamNameAlreadyRegistered);

        var memberInfos = string.Join(";", participants.Select(member =>
        {
            var memberName = (!string.IsNullOrWhiteSpace(member.Name) ? member.Name
                : member.UserId is int id ? GetTeamMemberName(membersById[id]) : null)
                ?? throw new OperationValidationException(SiteResources.RegistrationInformationIncorrect);
            return $"{memberName};{member.UserId}";
        }));

        var participant = new Competitor
        {
            Name = name,
            Contest = null!,
            ContestId = contestId,
            OrganizationId = organizationId,
            CoachId = coachId,
            IsOutOfCompetition = outOfCompetition,
            MemberInfos = memberInfos,
            RegistrationDate = timeProvider.GetLocalNow(),
            Members = [.. membersById.Values],
            Messages = [],
            Submissions = []
        };

        var result = await users.CreateAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);

        await transaction.CommitAsync(token);
    }

    public Task SetApprovalAsync(ClaimsPrincipal actor, int contestId, int participantId, bool approved,
        CancellationToken token = default) =>
        UpdateAsync(actor, contestId, participantId, participant => participant.IsApproved = approved, token);

    public Task SetOutOfCompetitionAsync(ClaimsPrincipal actor, int contestId, int participantId, bool outOfCompetition,
        CancellationToken token = default) =>
        UpdateAsync(actor, contestId, participantId, participant => participant.IsOutOfCompetition = outOfCompetition, token);

    public Task SetDisqualificationAsync(ClaimsPrincipal actor, int contestId, int participantId, bool disqualified,
        CancellationToken token = default) =>
        UpdateAsync(actor, contestId, participantId, participant => participant.IsDisqualified = disqualified, token);

    private async Task UpdateAsync(ClaimsPrincipal actor, int contestId, int participantId,
        Action<Competitor> update, CancellationToken token)
    {
        await AuthorizeAsync(actor, contestId);
        var participant = await context.Competitors.AsTracking()
            .SingleOrDefaultAsync(x => x.Id == participantId && x.ContestId == contestId, token)
            ?? throw new EntityNotFoundException();

        update(participant);

        var result = await users.UpdateAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);
    }

    public async Task DeleteAsync(ClaimsPrincipal actor, int contestId, int participantId,
        CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await DeleteCoreAsync(contestId, participantId, token);
    }

    public async Task CancelRegistrationAsync(ClaimsPrincipal actor, int contestId, int participantId,
        CancellationToken token = default)
    {
        var participant = await context.Competitors.AsNoTracking().Include(item => item.Contest).Include(item => item.Members)
            .SingleOrDefaultAsync(item => item.Id == participantId && item.ContestId == contestId, token);
        if (participant is null
            || !(await authorization.AuthorizeAsync(actor, participant, PolicyNames.CancelRegistration)).Succeeded)
            throw new OperationValidationException(SiteResources.RegistrationUpdateFailed);

        await DeleteCoreAsync(contestId, participantId, token);
    }

    private async Task DeleteCoreAsync(int contestId, int participantId,
        CancellationToken token = default)
    {
        var participant = await context.Competitors.AsTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == participantId && x.ContestId == contestId, token)
            ?? throw new EntityNotFoundException();

        if (await context.Submissions.AnyAsync(x => x.UserId == participantId, token)
            || await context.Messages.AnyAsync(x => x.UserId == participantId, token))
            throw new OperationValidationException(SiteResources.ParticipantDeletionHasHistory);

        participant.Members.Clear();

        var result = await users.DeleteAsync(participant);
        if (!result.Succeeded)
            throw new IdentityOperationException(result.Errors);
    }

    public async Task<int> DeleteWithoutSubmissionsAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token)
            ?? throw new EntityNotFoundException();

        if (timeProvider.GetLocalNow() <= contest.EndTime)
            throw new OperationValidationException(SiteResources.ParticipantCleanupAfterEndOnly);

        var participantIds = await context.Competitors
            .Where(x => x.ContestId == contestId && !x.Submissions.Any() && !x.Messages.Any())
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(token);

        foreach (var participantId in participantIds)
        {
            token.ThrowIfCancellationRequested();

            await DeleteCoreAsync(contestId, participantId, token);
        }

        await transaction.CommitAsync(token);

        return participantIds.Count;
    }

    public async Task ApproveAllAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
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

    public async Task<ParticipantImportSummary> ImportAsync(ClaimsPrincipal actor, int contestId,
        IReadOnlyCollection<string> names, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        if (names.Count == 0)
            throw new OperationValidationException(SiteResources.ParticipantFileEmpty);
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new OperationValidationException(SiteResources.ParticipantFileDuplicateNames);

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        if (contest.IsAcm)
            throw new OperationValidationException(SiteResources.ParticipantImportIndividualOnly);

        var existing = contest.Competitors.ToLookup(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();

            var count = existing[name].Count();
            if (count > 1)
                throw new OperationValidationException(string.Format(CultureInfo.CurrentCulture, SiteResources.ParticipantNameAmbiguous, name));
            if (count == 1)
                continue;

            var participant = new Competitor
            {
                ContestId = contestId,
                Contest = null!,
                Name = name,
                IsApproved = true,
                RegistrationDate = timeProvider.GetLocalNow(),
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

    public async Task AssignLoginsAsync(ClaimsPrincipal actor, int contestId, IReadOnlyDictionary<string, string> logins,
        CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await AssignLoginsCoreAsync(contestId, logins, token);
    }

    private async Task AssignLoginsCoreAsync(int contestId, IReadOnlyDictionary<string, string> logins,
        CancellationToken token = default)
    {
        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (login, password) in logins)
        {
            if (!available.TryAdd(login, password))
                throw new OperationValidationException(SiteResources.ParticipantLoginsDuplicate);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.AsTracking().Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        foreach (var participant in contest.Competitors)
            if (participant.UserName is { } username)
                available.Remove(username);

        if (available.Count < contest.Competitors.Count(x => x.UserName is null))
            throw new OperationValidationException(SiteResources.ParticipantLoginsTooFew);

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

    public async Task GenerateLoginsAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        int count = await context.Competitors.CountAsync(x => x.ContestId == contestId, token);
        var logins = new Dictionary<string, string>();
        for (int i = 1; i <= count; ++i)
            logins.Add($"olymp{i:000}", RandomNumberGenerator.GetString("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", 10));

        await AssignLoginsCoreAsync(contestId, logins, token);
    }

    public async Task RemoveLoginsAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);

        var contest = await context.Contests.AsTracking().Include(x => x.Competitors)
            .SingleOrDefaultAsync(x => x.Id == contestId, token) ?? throw new EntityNotFoundException();

        await RemoveLoginsCoreAsync(contest, token);
        await transaction.CommitAsync(token);
    }

    public async Task FinishContestAsync(ClaimsPrincipal actor, int contestId, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var contest = await context.Contests.AsTracking().Include(item => item.Competitors)
            .SingleOrDefaultAsync(item => item.Id == contestId, token) ?? throw new EntityNotFoundException();
        if (contest.EndTime >= timeProvider.GetUtcNow())
            throw new OperationValidationException(SiteResources.StaffContestMustEnd);

        contest.FreezeTime = null;
        contest.IsOpen = true;
        await RemoveLoginsCoreAsync(contest, token);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task RemoveLoginsCoreAsync(Contest contest, CancellationToken token)
    {
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
                result = await users.RemoveClaimAsync(participant, new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()));
            }
            finally
            {
                context.ChangeTracker.QueryTrackingBehavior = trackingBehavior;
            }

            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);
        }
    }
}
