using System.Globalization;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Staff;

public sealed record StaffSubmissionFilter(int? Contest = null, int? AuthorId = null, string? Author = null,
    int? Problem = null, string? ProblemSearch = null, int? Compiler = null, string? Language = null,
    string? IpAddress = null, string? Scope = null, int? StatusCode = null, string? Status = null, DateTime? From = null, DateTime? To = null)
{
    public IQueryable<Submission> Apply(OlympContext db, int userId)
    {

        if (From > To)
            throw new ArgumentException("The start date must not follow the end date.");
        var query = db.Submissions.AsNoTracking()
            .Where(item => Contest == null || item.Problem.ContestId == Contest);
        if (AuthorId is int authorId)
            query = query.Where(item => item.UserId == authorId);
        if (AuthorId is null && !string.IsNullOrWhiteSpace(Author))
            query = query.Where(item => item.User.Name.Contains(Author) || (item.User.UserName != null && item.User.UserName.Contains(Author)));
        if (Problem is int problem)
            query = query.Where(item => item.ProblemId == problem);
        if (Problem is null && !string.IsNullOrWhiteSpace(ProblemSearch))
        {
            string search = ProblemSearch.Trim();
            query = query.Where(item => item.Problem.Name.Contains(search));
        }
        if (Compiler is int compiler)
            query = query.Where(item => item.CompilatorId == compiler);
        if (!string.IsNullOrWhiteSpace(Language))
            query = query.Where(item => item.Compilator.Language == Language);
        if (!string.IsNullOrWhiteSpace(IpAddress))
        {
            string address = IpAddress.Trim();
            query = query.Where(item => item.CommiterInfo == address);
        }
        query = query.ForAuthors(Scope, userId, db, Contest);
        if (StatusCode is int exactStatus)
            query = query.Where(item => item.StatusCode == exactStatus);
        query = (StatusCode is null ? Status : null) switch
        {
            "accepted" => query.Where(item => item.StatusCode == 2),
            "compilation" => query.Where(item => item.StatusCode == -1),
            "wrong-answer" => query.Where(item => item.StatusCode < -1000 && item.StatusCode > -2000),
            "time-limit" => query.Where(item => item.StatusCode < -2000 && item.StatusCode > -3000),
            "runtime-error" => query.Where(item => item.StatusCode < -3000 && item.StatusCode > -4000),
            "presentation-error" => query.Where(item => item.StatusCode < -4000 && item.StatusCode > -5000),
            "idleness-limit" => query.Where(item => item.StatusCode < -5000 && item.StatusCode > -6000),
            "memory-limit" => query.Where(item => item.StatusCode < -6000 && item.StatusCode > -7000),
            "internal-error" => query.Where(item => item.StatusCode != 0 && item.StatusCode != 1 && item.StatusCode != 2 && item.StatusCode != -1
                && !(item.StatusCode < -1000 && item.StatusCode > -2000)
                && !(item.StatusCode < -2000 && item.StatusCode > -3000)
                && !(item.StatusCode < -3000 && item.StatusCode > -4000)
                && !(item.StatusCode < -4000 && item.StatusCode > -5000)
                && !(item.StatusCode < -5000 && item.StatusCode > -6000)
                && !(item.StatusCode < -6000 && item.StatusCode > -7000)),
            "queued" => query.Where(item => item.StatusCode == 0),
            "checking" => query.Where(item => item.StatusCode == 1),
            "pending" => query.Where(item => item.StatusCode == 0 || item.StatusCode == 1),
            "rejected" => query.Where(item => item.StatusCode != 0 && item.StatusCode != 1 && item.StatusCode != 2),
            _ => query
        };
        if (From is DateTime from)
            query = query.Where(item => item.CommitTime >= new DateTimeOffset(from));
        if (To is DateTime to)
            query = query.Where(item => item.CommitTime <= new DateTimeOffset(to));
        return query;
    }

    public static StaffSubmissionFilter FromList(string relativeUrl)
    {
        var uri = new Uri(new Uri("https://staff.invalid/"), relativeUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        int? contest = null;
        if (segments is ["jury", "contests", var contestId, "submissions"] && int.TryParse(contestId, out int id)) contest = id;
        else if (uri.AbsolutePath != "/jury/submissions") throw new ArgumentException("Invalid submission selection.");
        var query = QueryHelpers.ParseQuery(uri.Query);
        string? Text(string key) => query.TryGetValue(key, out var value) ? value.ToString() : null;
        int? Number(string key) => int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
        DateTime? Date(string key) => Text(key) is string value ? DateTime.Parse(value, CultureInfo.InvariantCulture) : null;
        return new(contest ?? Number("Contest"), Number("AuthorId"), Text("Author"), Number("Problem"), Text("ProblemSearch"),
            Number("Compiler"), Text("Language"), Text("IpAddress"), Text("Scope") ?? (Text("OnlyParticipants") == "true" ? "participants" : null),
            Number("StatusCode"), Text("Status"), Date("From"), Date("To"));
    }
}
