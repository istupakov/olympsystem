using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

[Collection("Contest management")]
public sealed class MaterialImportTests(ContestServiceFixture site) : IClassFixture<ContestServiceFixture>, IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ClaimsPrincipal Actor(string role) => new(new ClaimsIdentity([new(ClaimTypes.Role, role)], "Test"));
    private ClaimsPrincipal Admin { get; set; } = default!;
    public async ValueTask InitializeAsync()
    {
        var user = await site.AddUserAsync(RoleNames.Admin);
        Admin = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Role, RoleNames.Admin)], "Test"));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static (string, byte[]) Manifest(Contest contest) => ($"{contest.Abbr}/contest.json", JsonSerializer.SerializeToUtf8Bytes(new { id = contest.Id }));
    private async Task<IReadOnlyList<MaterialImportRow>> PreviewAsync(MaterialImportService service, byte[] zip, bool pdf = false)
    {
        using var stream = new MemoryStream(zip);
        return await service.PreviewAsync(Admin, stream, pdf, Token);
    }

    [Fact]
    public async Task UpdatesContestAndProblemNamesAndPartialParticipantFieldsWithoutChangingAccounts()
    {
        var contest = await site.AddContestAsync();
        var member = await site.AddUserAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        db.Attach(member);
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        var participant = TestEntities.CreateCompetitor(contest, "Old team");
        participant.UserName = "unchanged-login";
        participant.PasswordHash = "unchanged-hash";
        participant.Members.Add(member);
        participant.IsDisqualified = true;
        var untouched = TestEntities.CreateCompetitor(contest, "Untouched");
        db.Problems.Add(problem);
        db.Competitors.AddRange(participant, untouched);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        byte[] zip = ProblemArchives.Zip(
            ($"{contest.Abbr}/contest.json", JsonSerializer.SerializeToUtf8Bytes(new { id = contest.Id, name = "Renamed contest" })),
            ($"{contest.Abbr}/problem01/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id, name = "Ignored JSON title" })),
            ($"{contest.Abbr}/problem01/statement.md", Encoding.UTF8.GetBytes(problem.Text)),
            ($"{contest.Abbr}/participants.jsonl", JsonSerializer.SerializeToUtf8Bytes(new { id = participant.Id, name = "Renamed team", isApproved = false, isOutOfCompetition = true })));
        var row = Assert.Single(await PreviewAsync(service, zip));
        Assert.Null(row.Error);
        Assert.Equal(5, row.Changes.Count);
        Assert.Contains(row.Changes, c => c.Before == "Old team" && c.After == "Renamed team");
        Assert.Equal("Old team", await db.Competitors.Where(p => p.Id == participant.Id).Select(p => p.Name).SingleAsync(Token));
        await service.SaveAsync(Admin, row, Token);
        db.ChangeTracker.Clear();
        var savedContest = await db.Contests.SingleAsync(c => c.Id == contest.Id, Token);
        var saved = await db.Competitors.Include(p => p.Members).SingleAsync(p => p.Id == participant.Id, Token);
        Assert.Equal("Renamed contest", savedContest.Name);
        Assert.Equal("Двоичная строка", await db.Problems.Where(p => p.Id == problem.Id).Select(p => p.Name).SingleAsync(Token));
        Assert.Equal("Renamed team", saved.Name);
        Assert.False(saved.IsApproved);
        Assert.True(saved.IsOutOfCompetition);
        Assert.True(saved.IsDisqualified);
        Assert.Equal("unchanged-login", saved.UserName);
        Assert.Equal("unchanged-hash", saved.PasswordHash);
        Assert.Equal(member.Name, Assert.Single(saved.Members).Name);
        Assert.Equal("Untouched", await db.Competitors.Where(p => p.Id == untouched.Id).Select(p => p.Name).SingleAsync(Token));
        Assert.False(Assert.Single(await PreviewAsync(service, zip)).CanSave);

        zip = ProblemArchives.Zip(Manifest(contest), ($"{contest.Abbr}/participants.jsonl",
            JsonSerializer.SerializeToUtf8Bytes(new { id = participant.Id, isApproved = true, isOutOfCompetition = false, isDisqualified = false })));
        row = Assert.Single(await PreviewAsync(service, zip));
        Assert.Equal(3, row.Changes.Count);
        await service.SaveAsync(Admin, row, Token);
        saved = await db.Competitors.SingleAsync(p => p.Id == participant.Id, Token);
        Assert.Equal("Renamed team", saved.Name);
        Assert.True(saved.IsApproved);
        Assert.False(saved.IsOutOfCompetition);
        Assert.False(saved.IsDisqualified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsForeignAndUnknownParticipantsWithoutApplyingContestChanges(bool unknown)
    {
        var contest = await site.AddContestAsync();
        var other = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(other);
        var participant = TestEntities.CreateCompetitor(other, "Other team");
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var row = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(
            ($"{contest.Abbr}/contest.json", JsonSerializer.SerializeToUtf8Bytes(new { id = contest.Id, name = "Must not save" })),
            ($"{contest.Abbr}/participants.jsonl", JsonSerializer.SerializeToUtf8Bytes(new { id = unknown ? int.MaxValue : participant.Id, name = "Must not save" })))));
        Assert.NotNull(row.Error);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(Admin, row, Token));
        Assert.Equal(contest.Name, await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Name).SingleAsync(Token));
        Assert.Equal("Other team", await db.Competitors.Where(p => p.Id == participant.Id).Select(p => p.Name).SingleAsync(Token));
    }

    [Fact]
    public async Task ConcurrentParticipantChangesRejectEntireContestAndEmptyListsDoNotDelete()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var participant = TestEntities.CreateCompetitor(contest, "Team");
        db.Competitors.Add(participant);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        byte[] zip = ProblemArchives.Zip(
            ($"{contest.Abbr}/contest.json", JsonSerializer.SerializeToUtf8Bytes(new { id = contest.Id, name = "Must not save" })),
            ($"{contest.Abbr}/participants.jsonl", JsonSerializer.SerializeToUtf8Bytes(new { id = participant.Id, isDisqualified = true })));
        var row = Assert.Single(await PreviewAsync(service, zip));
        await db.Competitors.Where(p => p.Id == participant.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Concurrent name"), Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(Admin, row, Token));
        Assert.Equal(contest.Name, await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Name).SingleAsync(Token));
        Assert.False(await db.Competitors.Where(p => p.Id == participant.Id).Select(p => p.IsDisqualified).SingleAsync(Token));
        row = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest), ($"{contest.Abbr}/participants.jsonl", []))));
        Assert.False(row.CanSave);
        Assert.Equal(1, await db.Competitors.CountAsync(p => p.ContestId == contest.Id, Token));
    }

    [Fact]
    public async Task EmptyStatementRetainsProblemNameAndDuplicateParticipantNamesAreRejected()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        var participant = TestEntities.CreateCompetitor(contest, "First");
        db.Problems.Add(problem);
        db.Competitors.AddRange(participant, TestEntities.CreateCompetitor(contest, "Second"));
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var row = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/problem01/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id })),
            ($"{contest.Abbr}/problem01/statement.md", []))));
        await service.SaveAsync(Admin, row, Token);
        var stored = await db.Problems.SingleAsync(p => p.Id == problem.Id, Token);
        Assert.Equal(problem.Name, stored.Name);
        Assert.Null(stored.Text);
        row = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/participants.jsonl", JsonSerializer.SerializeToUtf8Bytes(new { id = participant.Id, name = "second" })))));
        Assert.NotNull(row.Error);
        Assert.False(row.CanSave);
    }

    [Fact]
    public async Task PartialImportUsesIdsUpdatesTextAddsAndDeletesTablesAndRetainsUnrelatedData()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        contest.Description = "Old description";
        contest.FinalTableText = "<table>Old final</table>";
        contest.ProblemPdf = new Blob { Data = "%PDF-original"u8.ToArray() };
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = "Old text";
        problem.Tests.Add(new Test { Number = 1, Problem = problem, IsActive = true, Input = [1], Output = [2], Score = 100 });
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        int testId = problem.Tests.Single().Id;
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var zip = ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/description.html", "Описание"u8.ToArray()),
            ($"{contest.Abbr}/tables/final.html", " \n"u8.ToArray()),
            ($"{contest.Abbr}/tables/official.html", "<table>Официальная</table>"u8.ToArray()),
            ($"{contest.Abbr}/problem99/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id, timeLimit = 999 })),
            ($"{contest.Abbr}/problem99/statement.md", Encoding.UTF8.GetBytes(Olymp.Tests.Statements.StatementTests.Source)),
            ($"{contest.Abbr}/statements.pdf", "%PDF-ignored"u8.ToArray()));
        var row = Assert.Single(await PreviewAsync(service, zip));
        Assert.Null(row.Error);
        Assert.Equal(5, row.Changes.Count);
        Assert.True(row.Changes.Single(c => c.Name == "tables/final.html").IsDeletion);
        Assert.Equal("Old description", await db.Contests.AsNoTracking().Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(Token));
        await service.SaveAsync(Admin, row, Token);
        db.ChangeTracker.Clear();
        var stored = await db.Contests.Include(c => c.FinalTable).Include(c => c.OfficialTable).Include(c => c.ProblemPdf)
            .Include(c => c.Problems).ThenInclude(p => p.Tests).SingleAsync(c => c.Id == contest.Id, Token);
        Assert.True(row.Saved);
        Assert.Equal("Описание", stored.Description);
        Assert.Null(stored.FinalTableId);
        Assert.Equal("<table>Официальная</table>", stored.OfficialTableText);
        Assert.Equal("%PDF-original"u8.ToArray(), stored.ProblemPdf!.Data);
        var savedProblem = Assert.Single(stored.Problems);
        Assert.Equal(Olymp.Tests.Statements.StatementTests.Source, savedProblem.Text);
        Assert.Equal(problem.TimeLimit, savedProblem.TimeLimit);
        Assert.Equal(testId, Assert.Single(savedProblem.Tests).Id);
        var unchanged = Assert.Single(await PreviewAsync(service, zip));
        Assert.Empty(unchanged.Changes);
        Assert.False(unchanged.CanSave);
    }

    [Fact]
    public async Task CrossContestProblemAndUnknownContestAreRejectedWithoutBlockingValidRows()
    {
        var contest = await site.AddContestAsync();
        var other = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(other);
        var problem = TestEntities.CreateProblem(other);
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var rows = await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest), Manifest(other),
            ($"{contest.Abbr}/description.html", "must not save"u8.ToArray()),
            ($"{contest.Abbr}/problem01/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id })),
            ($"{contest.Abbr}/problem01/statement.md", []),
            ($"{other.Abbr}/description.html", "valid"u8.ToArray()),
            ("unknown/contest.json", "{\"id\":2147483647}"u8.ToArray())));
        Assert.NotNull(rows.Single(r => r.ContestId == contest.Id).Error);
        Assert.NotNull(rows.Single(r => r.ContestId == int.MaxValue).Error);
        var valid = rows.Single(r => r.ContestId == other.Id);
        await service.SaveAsync(Admin, valid, Token);
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(Token));
        Assert.Equal("valid", await db.Contests.Where(c => c.Id == other.Id).Select(c => c.Description).SingleAsync(Token));
    }

    [Fact]
    public async Task ConcurrentEditPreventsAllChangesAndJuryCannotPreviewOrSave()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        byte[] zip = ProblemArchives.Zip(Manifest(contest), ($"{contest.Abbr}/description.html", "imported"u8.ToArray()),
            ($"{contest.Abbr}/tables/final.html", "<table>new</table>"u8.ToArray()));
        using var forbidden = new MemoryStream(zip);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(Actor(RoleNames.Jury), forbidden, false, Token));
        var row = Assert.Single(await PreviewAsync(service, zip));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(Actor(RoleNames.Jury), row, Token));
        await db.Contests.Where(c => c.Id == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Description, "concurrent"), Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(Admin, row, Token));
        Assert.Equal("concurrent", await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(Token));
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.FinalTableId).SingleAsync(Token));
    }

    [Fact]
    public async Task RejectsLossyTableEncodingAndInvalidMarkdownButAcceptsEmptyText()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        contest.Description = "description";
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = "text";
        db.Problems.Add(problem);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var badTable = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/tables/final.html", "<table>😀</table>"u8.ToArray()))));
        Assert.NotNull(badTable.Error);
        var invalid = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/problem01/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id })),
            ($"{contest.Abbr}/problem01/statement.md", "not a statement"u8.ToArray()))));
        Assert.NotNull(invalid.Error);
        var empty = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/description.html", []),
            ($"{contest.Abbr}/problem01/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { id = problem.Id })),
            ($"{contest.Abbr}/problem01/statement.md", []))));
        await service.SaveAsync(Admin, empty, Token);
        Assert.Null(await db.Problems.Where(p => p.Id == problem.Id).Select(p => p.Text).SingleAsync(Token));
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(Token));
    }

    [Fact]
    public async Task GeneratesPdfBeforeSavingEvenForUnchangedStatementsAndDetectsChangedExamples()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Attach(contest);
        var problem = TestEntities.CreateProblem(contest);
        problem.Text = Olymp.Tests.Statements.StatementTests.Source;
        problem.Tests.Add(new Test { Number = 1, Problem = problem, IsActive = true, IsOpen = true, Input = "1\n"u8.ToArray(), Output = "2\n"u8.ToArray() });
        var inactive = TestEntities.CreateProblem(contest, 2);
        inactive.IsActive = false;
        db.Problems.AddRange(problem, inactive);
        await db.SaveChangesAsync(Token);
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var zip = ProblemArchives.Zip(Manifest(contest));
        var row = Assert.Single(await PreviewAsync(service, zip, true));
        Assert.Null(row.Error);
        Assert.True(Assert.Single(row.Changes).IsPdf);
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.ProblemPdfId).SingleAsync(Token));
        await db.Tests.Where(t => t.ProblemId == problem.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Input, "*"u8.ToArray()), Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(Admin, row, Token));
        row = Assert.Single(await PreviewAsync(service, zip, true));
        await service.SaveAsync(Admin, row, Token);
        byte[] stored = await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.ProblemPdf!.Data).SingleAsync(Token);
        Assert.True(stored.AsSpan().StartsWith("%PDF-"u8));
    }

    [Fact]
    public async Task PdfFailureLeavesDescriptionAndTablesUntouched()
    {
        var contest = await site.AddContestAsync();
        await using var scope = site.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MaterialImportService>();
        var row = Assert.Single(await PreviewAsync(service, ProblemArchives.Zip(Manifest(contest),
            ($"{contest.Abbr}/description.html", "new"u8.ToArray()), ($"{contest.Abbr}/tables/final.html", "<table>new</table>"u8.ToArray())), true));
        Assert.NotNull(row.Error);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(Admin, row, Token));
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.Description).SingleAsync(Token));
        Assert.Null(await db.Contests.Where(c => c.Id == contest.Id).Select(c => c.FinalTableId).SingleAsync(Token));
    }
}
