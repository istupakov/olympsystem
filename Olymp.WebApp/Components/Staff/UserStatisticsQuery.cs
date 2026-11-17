using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Identity;

namespace Olymp.WebApp.Components.Staff;

public static class UserStatisticsQuery
{
    public static string? Label(string? metric) => metric switch
    {
        "all" => SiteResources.UserStatisticsTotal,
        "new" => SiteResources.UserStatisticsNew,
        "active" => SiteResources.UserStatisticsActive,
        "new-active" => SiteResources.UserStatisticsNewActive,
        "confirmed" => SiteResources.UserStatisticsConfirmed,
        "unconfirmed" => SiteResources.StaffEmailNotConfirmed,
        "blocked" => SiteResources.StaffBanned,
        "migration" => SiteResources.UserStatisticsMigration,
        "legacy" => SiteResources.UserStatisticsLegacy,
        "current-modern" => SiteResources.UserStatisticsCurrentModern,
        "current-legacy" => SiteResources.UserStatisticsCurrentLegacy,
        "migration-modern" => SiteResources.UserStatisticsMigrationModern,
        "migration-legacy" => SiteResources.UserStatisticsMigrationLegacy,
        "no-password" => SiteResources.UserStatisticsNoPassword,
        "cleanup" => SiteResources.UserStatisticsCleanup,
        _ => null
    };

    public static IQueryable<OlympUser> Apply(IQueryable<OlympUser> query, OlympContext db,
        string? metric, DateTimeOffset from, DateTimeOffset until)
    {
        if (metric is null or "all") return query;
        if (metric == "cleanup")
        {
            var candidates = UnconfirmedUserCleanupService.Candidates(db, until.AddMonths(-1)).Select(item => item.Id);
            return query.Where(item => candidates.Contains(item.Id));
        }
        if (metric is "new" or "new-active")
            query = query.Where(item => item.RegistrationDate >= from && item.RegistrationDate <= until);
        if (metric is "active" or "new-active")
            query = query.Where(item => item.Submissions.Any(submission => submission.CommitTime >= from && submission.CommitTime <= until));
        if (metric is "migration" or "migration-modern" or "migration-legacy")
            query = query.Where(item => item.NormalizedEmail == null || item.NormalizedEmail == "" || item.NormalizedUserName != item.NormalizedEmail);
        if (metric is "current-modern" or "current-legacy")
            query = query.Where(item => item.NormalizedEmail != null && item.NormalizedEmail != "" && item.NormalizedUserName == item.NormalizedEmail);
        if (metric is "legacy" or "current-legacy" or "migration-legacy")
            query = query.Where(item => item.PasswordHash != null && item.PasswordHash.Length - item.PasswordHash.Replace("|", "").Length == 2);
        if (metric is "current-modern" or "migration-modern")
            query = query.Where(item => item.PasswordHash != null && item.PasswordHash != "" && item.PasswordHash.Length - item.PasswordHash.Replace("|", "").Length != 2);
        return metric switch
        {
            "confirmed" => query.Where(item => item.EmailConfirmed),
            "unconfirmed" => query.Where(item => !item.EmailConfirmed),
            "blocked" => query.Where(item => item.LockoutEnabled && item.LockoutEnd > until),
            "no-password" => query.Where(item => item.PasswordHash == null || item.PasswordHash == ""),
            _ => Label(metric) is null ? query.Where(item => false) : query
        };
    }

    public static async Task<Dictionary<string, int>> CountAsync(OlympContext db, DateTimeOffset from,
        DateTimeOffset until, CancellationToken token = default)
    {
        var counts = new Dictionary<string, int>();
        foreach (string metric in new[] { "all", "new", "active", "new-active", "confirmed", "unconfirmed", "blocked",
            "current-modern", "current-legacy", "migration-modern", "migration-legacy", "no-password", "cleanup" })
            counts[metric] = await Apply(db.OlympUsers.AsNoTracking(), db, metric, from, until).CountAsync(token);
        return counts;
    }
}
