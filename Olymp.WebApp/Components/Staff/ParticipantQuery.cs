using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Staff;

public sealed record ParticipantQuery(int? ContestId, string? Search = null, int? Organization = null,
    bool? Approved = null, bool? Unofficial = null, bool? Disqualified = null, int? Coach = null, int? Member = null, string? CoachSearch = null, string? MemberSearch = null)
{
    public IQueryable<Competitor> Apply(OlympContext db)
    {
        var query = db.Competitors.AsNoTracking();
        if (ContestId is int contestId) query = query.Where(item => item.ContestId == contestId);
        if (Coach is int coach) query = query.Where(item => item.CoachId == coach);
        if (Member is int member) query = query.Where(item => item.Members.Any(account => account.Id == member));
        if (!string.IsNullOrWhiteSpace(CoachSearch))
        {
            string search = CoachSearch.Trim();
            query = query.Where(item => item.Coach != null && item.Coach.Name.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(MemberSearch))
        {
            string search = MemberSearch.Trim();
            query = query.Where(item => item.Members.Any(account => account.Name.Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(Search))
        {
            string search = Search.Trim();
            query = query.Where(item => item.Name.Contains(search) || (item.UserName != null && item.UserName.Contains(search)));
        }
        if (Organization is int organization) query = query.Where(item => item.OrganizationId == organization);
        if (Approved is bool approved) query = query.Where(item => item.IsApproved == approved);
        if (Unofficial is bool unofficial) query = query.Where(item => item.IsOutOfCompetition == unofficial);
        if (Disqualified is bool disqualified) query = query.Where(item => item.IsDisqualified == disqualified);
        return query;
    }

    public static IOrderedQueryable<Competitor> Order(IQueryable<Competitor> query, string? sort, bool descending) =>
        (sort, descending) switch
        {
            ("ID", false) => query.OrderBy(item => item.Id),
            ("ID", true) => query.OrderByDescending(item => item.Id),
            ("Name", false) => query.OrderBy(item => item.Name).ThenBy(item => item.Id),
            ("Name", true) => query.OrderByDescending(item => item.Name).ThenBy(item => item.Id),
            ("Login", false) => query.OrderBy(item => item.UserName).ThenBy(item => item.Id),
            ("Login", true) => query.OrderByDescending(item => item.UserName).ThenBy(item => item.Id),
            (_, true) => query.OrderByDescending(item => item.RegistrationDate).ThenBy(item => item.Id),
            _ => query.OrderBy(item => item.RegistrationDate).ThenBy(item => item.Id)
        };
}
