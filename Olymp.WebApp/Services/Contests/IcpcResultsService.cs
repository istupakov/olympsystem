using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Icpc;
using Olymp.WebApp.Components.Contests;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed record IcpcResultsPreview(IcpcResults Results, string Snapshot, IReadOnlyList<string> InvalidTeamIds);

public sealed class IcpcResultsService(IDbContextFactory<OlympContext> factory, TimeProvider clock)
{
    public async Task<IcpcResultsPreview> PreviewAsync(int contestId, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (!await db.Contests.AnyAsync(c => c.Id == contestId, token)) throw new EntityNotFoundException();
        var contest = await StandingQuery.LoadAsync(db, contestId, token);
        if (!contest.IsAcm || clock.GetUtcNow() < contest.EndTime)
            throw new OperationValidationException(SiteResources.IcpcResultsUnavailable);
        var standing = Standing.NewStanding(contest, false, true, true, null, clock.GetUtcNow()).ToArray();
        var ids = standing.Select(r => r.User.Id).ToArray();
        var submissions = contest.Problems.SelectMany(p => p.Submissions).Where(s => ids.Contains(s.UserId)
            && !s.IsHidden && s.CommitTime >= contest.StartTime && s.CommitTime <= contest.EndTime).ToArray();
        if (submissions.Any(s => s.StatusCode is 0 or 1))
            throw new OperationValidationException(SiteResources.IcpcResultsPending);
        var claims = await db.UserClaims.AsNoTracking().Where(c => ids.Contains(c.UserId) && c.ClaimType == ClaimNames.IcpcTeamId)
            .OrderBy(c => c.Id).Select(c => new { c.UserId, c.ClaimValue }).ToArrayAsync(token);
        var external = ids.ToDictionary(id => id, id =>
        {
            var values = claims.Where(c => c.UserId == id).Select(c => c.ClaimValue).ToArray();
            return values.Length == 1 && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                && value > 0 ? value.ToString(CultureInfo.InvariantCulture) : null;
        });
        var duplicates = external.Values.Where(v => v is not null).GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (int id in ids.Where(id => duplicates.Contains(external[id]))) external[id] = null;
        var attempts = submissions.OrderBy(s => s.CommitTime).ThenBy(s => s.Id).ToLookup(s => (s.UserId, s.ProblemId));
        int Judged(int userId, int problemId)
        {
            var taskAttempts = attempts[(userId, problemId)].ToArray();
            int firstCorrect = Array.FindIndex(taskAttempts, s => s.StatusCode == 2 || s.Score > 0);
            return taskAttempts.Take(firstCorrect < 0 ? taskAttempts.Length : firstCorrect + 1).Count(s => s.StatusCode != -10);
        }
        int rank = 0;
        var teams = standing.Select((row, index) =>
        {
            if (index == 0 || row.Score != standing[index - 1].Score || row.SummaryTime != standing[index - 1].SummaryTime
                || row.LastSuccessTime != standing[index - 1].LastSuccessTime) rank = index + 1;
            return new IcpcResultTeam(row.User.Id.ToString(CultureInfo.InvariantCulture), row.User.Name, external[row.User.Id], rank,
                row.ProblemResults.Count(p => p.IsSuccess), row.SummaryTime, row.LastSuccessTime,
                [.. row.ProblemResults.Select(p => new IcpcProblemScore(p.Problem.Id.ToString(CultureInfo.InvariantCulture), Judged(row.User.Id, p.Problem.Id),
                    p.IsSuccess, p.IsSuccess ? p.LastCommitTime - contest.StartTime : null))]);
        }).ToArray();
        var metadata = await db.Problems.AsNoTracking().Where(p => p.ContestId == contestId)
            .Select(p => new { p.Id, p.TimeLimit, p.MemoryLimit, TestCount = p.Tests.Count(t => t.IsActive) }).ToDictionaryAsync(p => p.Id, token);
        var results = new IcpcResults(contest.Id.ToString(CultureInfo.InvariantCulture), contest.Name, contest.StartTime, contest.EndTime,
            [.. contest.Problems.OrderBy(p => p.Number).Select((p, i) => new IcpcResultProblem(p.Id.ToString(CultureInfo.InvariantCulture),
                p.Number.ToString(CultureInfo.InvariantCulture), i, metadata[p.Id].TimeLimit, metadata[p.Id].MemoryLimit, metadata[p.Id].TestCount))], teams);
        // Include raw relevant submissions and claims: changes require a fresh preview even when ranks stay the same.
        string snapshot = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            results,
            submissions = submissions.OrderBy(s => s.Id).Select(s => new { s.Id, s.UserId, s.ProblemId, s.CommitTime, s.StatusCode, s.Score }),
            claims
        })));
        return new(results, snapshot, [.. teams.Where(t => t.IcpcId is null).Select(t => t.Id)]);
    }
}
