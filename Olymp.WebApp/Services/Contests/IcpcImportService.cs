using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Icpc;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed record IcpcAccountMatch(string Name, string? Email, int? UserId);
public sealed record IcpcOrganizationOption(int Id, string Name);
public sealed record IcpcCoachOption(int Id, string Name, string? Email, IReadOnlyList<IcpcOrganizationOption> Organizations);

public sealed class IcpcImportRow
{
    public required IcpcTeam Source { get; init; }
    public int? ExistingId { get; init; }
    public Competitor? ExistingTeam { get; init; }
    public string? ExistingDescription { get; init; }
    public required string Snapshot { get; init; }
    public required IReadOnlyList<IcpcAccountMatch> Members { get; init; }
    public required IReadOnlyList<IcpcCoachOption> Coaches { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public int? OrganizationId { get; set; }
    public int? CoachId
    {
        get;
        set
        {
            field = value;
            if (!Organizations.Any(org => org.Id == OrganizationId))
                OrganizationId = Organizations.Count == 1 ? Organizations[0].Id : null;
        }
    }
    public IReadOnlyList<IcpcOrganizationOption> Organizations => Coaches.SingleOrDefault(coach => coach.Id == CoachId)?.Organizations ?? [];
    public bool Selected { get; set; }
    public bool IsApproved => Source.Status == "ACCEPTED";
    public bool NeedsOrganization => !Organizations.Any(org => org.Id == OrganizationId);
    public bool NeedsCoach => !Coaches.Any(coach => coach.Id == CoachId);
    public bool HasNoRemarks => IsApproved && Errors.Count == 0 && Warnings.Count == 0
        && !NeedsOrganization && !NeedsCoach
        && (string.IsNullOrWhiteSpace(Source.EligibilityStatus) || Source.EligibilityStatus == "NO_ISSUES_FOUND")
        && string.IsNullOrWhiteSpace(Source.EligibilityIssue);
}

public sealed record IcpcImportPreview(IReadOnlyList<IcpcImportRow> Rows);
public sealed record IcpcImportSummary(int Added, int Updated);

/// <summary>Authorizes imports and revalidates the preview before any writes.</summary>
public sealed class IcpcImportService(OlympContext context, UserManager<User> users, IAuthorizationService authorization)
{
    public async Task<IcpcImportPreview> PreviewAsync(ClaimsPrincipal actor, int contestId,
        IReadOnlyList<IcpcTeam> teams, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId, token);
        return await BuildPreviewAsync(contestId, teams, token);
    }

    private async Task AuthorizeAsync(ClaimsPrincipal actor, int contestId, CancellationToken token)
    {
        if (!(await authorization.AuthorizeAsync(actor, contestId, PolicyNames.ManageContest)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        var contest = await context.Contests.SingleOrDefaultAsync(item => item.Id == contestId, token)
            ?? throw new EntityNotFoundException();
        if (!contest.IsIcpc)
            throw new OperationValidationException(SiteResources.IcpcOnly);
    }

    private async Task<IcpcImportPreview> BuildPreviewAsync(int contestId, IReadOnlyList<IcpcTeam> teams, CancellationToken token)
    {
        if (teams.Any(team => team.Id <= 0) || teams.Select(team => team.Id).Distinct().Count() != teams.Count)
            throw new OperationValidationException(SiteResources.IcpcInvalidData);
        var organizations = await context.Organizations.AsNoTracking().OrderBy(item => item.Name).ToListAsync(token);
        var existing = await context.Competitors.AsNoTracking().Where(item => item.ContestId == contestId)
            .Include(item => item.Members).Include(item => item.Organization).Include(item => item.Coach).AsSplitQuery().ToListAsync(token);
        var existingIds = existing.Select(item => item.Id).ToArray();
        var claims = await context.UserClaims.Where(claim => existingIds.Contains(claim.UserId)
            && claim.ClaimType == ClaimNames.IcpcTeamId).ToListAsync(token);
        var emails = teams.SelectMany(team => team.Members).Where(member => member.IsContestant).Select(member => users.NormalizeEmail(member.Username?.Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var accounts = await context.OlympUsers.AsNoTracking().Where(user => emails.Contains(user.NormalizedEmail)).ToListAsync(token);
        var coachAccounts = await context.OlympUsers.AsNoTracking()
            .Where(user => context.UserClaims.Any(claim => claim.UserId == user.Id && claim.ClaimType == ClaimNames.CoachOrg))
            .OrderBy(user => user.Name).ThenBy(user => user.Id).ToListAsync(token);
        var coachClaims = await context.UserClaims.Where(claim => claim.ClaimType == ClaimNames.CoachOrg).ToListAsync(token);
        var coachOrganizations = coachClaims.ToLookup(claim => claim.UserId, claim => claim.ClaimValue);
        var coaches = coachAccounts.Select(account => new IcpcCoachOption(account.Id, account.Name, account.Email,
            [.. organizations.Where(org => coachOrganizations[account.Id].Contains(org.Id.ToString(CultureInfo.InvariantCulture)))
                .Select(org => new IcpcOrganizationOption(org.Id, org.Name))]))
            .Where(coach => coach.Organizations.Count > 0).ToArray();
        var rows = new List<IcpcImportRow>();
        foreach (var team in teams)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var matches = claims.Where(claim => claim.ClaimValue == team.Id.ToString(CultureInfo.InvariantCulture))
                .Select(claim => claim.UserId).Distinct().ToArray();
            var current = matches.Length == 1 ? existing.Single(item => item.Id == matches[0]) : null;
            if (matches.Length > 1 || (current is not null && claims.Count(claim => claim.UserId == current.Id) != 1))
                errors.Add(SiteResources.IcpcDuplicateId);
            if (string.IsNullOrWhiteSpace(team.Name) || team.Members.Count(member => member.IsContestant) is < 1 or > 3
                || team.Members.Where(member => member.IsContestant).Any(member => string.IsNullOrWhiteSpace(member.Name) || member.Name.Contains(';')))
                errors.Add(SiteResources.IcpcInvalidData);
            if (existing.Any(item => item.Id != current?.Id && string.Equals(item.Name.Trim(), team.Name?.Trim(), StringComparison.OrdinalIgnoreCase)))
                errors.Add(SiteResources.TeamNameAlreadyRegistered);

            IcpcAccountMatch Match(IcpcMember member)
            {
                string? email = member.Username?.Trim();
                string? normalized = users.NormalizeEmail(email);
                var found = string.IsNullOrWhiteSpace(normalized) ? [] : accounts.Where(user => user.NormalizedEmail == normalized).ToArray();
                if (found.Length > 1)
                    errors.Add(string.Format(SiteResources.IcpcEmailAmbiguous, email));
                return new(member.Name, email, found.Length == 1 ? found[0].Id : null);
            }

            var members = team.Members.Where(member => member.IsContestant).Select(Match).ToArray();
            var coachEmails = team.Members.Where(member => member.IsCoach).Select(member => users.NormalizeEmail(member.Username?.Trim()))
                .Where(email => !string.IsNullOrWhiteSpace(email)).ToArray();
            var coachIds = coachAccounts.Where(account => coachEmails.Contains(account.NormalizedEmail) && coaches.Any(coach => coach.Id == account.Id))
                .Select(account => account.Id).ToArray();
            if (members.Where(member => member.UserId.HasValue).Select(member => member.UserId).Distinct().Count()
                != members.Count(member => member.UserId.HasValue))
                errors.Add(SiteResources.ParticipantsMustBeDifferent);
            int? coachId = coachIds.Length == 1 ? coachIds[0] : null;
            string snapshot = JsonSerializer.Serialize(new
            {
                Current = current is null ? null : new
                {
                    current.Id,
                    current.Name,
                    current.OrganizationId,
                    current.CoachId,
                    current.MemberInfos,
                    current.IsApproved,
                    Members = current.Members.Select(member => member.Id).Order().ToArray()
                },
                Members = members
            });
            rows.Add(new()
            {
                Source = team,
                ExistingId = current?.Id,
                ExistingTeam = current,
                ExistingDescription = current is null ? null : $"{current.Name}; {current.Organization?.Name}; "
                    + string.Join(", ", current.MemberNames ?? []) + $"; {current.Coach?.Name}",
                Snapshot = snapshot,
                Members = members,
                Coaches = coaches,
                CoachId = coachId,
                Selected = team.Status != "CANCELED" && errors.Count == 0,
                Errors = errors,
                Warnings = warnings
            });
        }
        return new(rows);
    }

    public async Task<IcpcImportSummary> ImportAsync(ClaimsPrincipal actor, int contestId,
        IReadOnlyList<IcpcImportRow> rows, CancellationToken token = default)
    {
        var selected = rows.Where(row => row.Selected).ToArray();
        if (selected.Length == 0)
            throw new OperationValidationException(SiteResources.IcpcSelectTeams);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await AuthorizeAsync(actor, contestId, token);
        var refreshed = await BuildPreviewAsync(contestId, [.. selected.Select(row => row.Source)], token);
        foreach (var row in selected)
        {
            var fresh = refreshed.Rows.Single(item => item.Source.Id == row.Source.Id);
            if (fresh.Snapshot != row.Snapshot)
                throw new OperationValidationException(SiteResources.IcpcPreviewChanged);
            if (fresh.Errors.Count != 0)
                throw new OperationValidationException(string.Join(" ", fresh.Errors));
            var coach = fresh.Coaches.SingleOrDefault(coach => coach.Id == row.CoachId)
                ?? throw new OperationValidationException(SiteResources.IcpcSelectCoach);
            if (!coach.Organizations.Any(org => org.Id == row.OrganizationId))
                throw new OperationValidationException(SiteResources.IcpcSelectOrganization);
        }
        if (selected.Select(row => row.Source.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new OperationValidationException(SiteResources.TeamNameAlreadyRegistered);
        var memberIds = selected.SelectMany(row => row.Members).Where(member => member.UserId.HasValue).Select(member => member.UserId!.Value).ToArray();
        var updatingIds = selected.Where(row => row.ExistingId.HasValue).Select(row => row.ExistingId!.Value).ToArray();
        if (memberIds.Distinct().Count() != memberIds.Length || await context.Competitors
            .AnyAsync(item => item.ContestId == contestId && !updatingIds.Contains(item.Id) && item.Members.Any(member => memberIds.Contains(member.Id)), token))
            throw new OperationValidationException(SiteResources.ParticipantsAlreadyRegistered);

        var accounts = await context.OlympUsers.AsTracking().Where(user => memberIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, token);
        int added = 0;
        foreach (var row in selected)
        {
            bool create = row.ExistingId is null;
            var team = create ? new Competitor
            {
                Name = row.Source.Name.Trim(),
                ContestId = contestId,
                Contest = null!,
                RegistrationDate = DateTimeOffset.Now,
                Members = [],
                Messages = [],
                Submissions = []
            } : await context.Competitors.AsTracking().Include(item => item.Members)
                .SingleAsync(item => item.Id == row.ExistingId && item.ContestId == contestId, token);
            team.Name = row.Source.Name.Trim();
            team.OrganizationId = row.OrganizationId;
            team.CoachId = row.CoachId;
            team.IsApproved = row.IsApproved;
            team.MemberInfos = string.Join(";", row.Members.Select(member => $"{member.Name};{member.UserId}"));
            team.Members.Clear();
            foreach (var member in row.Members.Where(member => member.UserId.HasValue))
                team.Members.Add(accounts[member.UserId!.Value]);
            var result = create ? await users.CreateAsync(team) : await users.UpdateAsync(team);
            if (!result.Succeeded)
                throw new IdentityOperationException(result.Errors);
            if (create)
            {
                result = await users.AddClaimAsync(team, new(ClaimNames.IcpcTeamId, row.Source.Id.ToString(CultureInfo.InvariantCulture)));
                if (!result.Succeeded)
                    throw new IdentityOperationException(result.Errors);
                added++;
            }
        }
        await transaction.CommitAsync(token);
        return new(added, selected.Length - added);
    }
}
