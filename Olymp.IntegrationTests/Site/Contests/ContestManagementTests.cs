using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services.Contests;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class ContestManagementTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Theory]
    [InlineData("en", "The file contains duplicate participant names.", "Added participants: 2; already existed: 0.")]
    [InlineData("ru", "В файле повторяются имена участников.", "Добавлено участников: 2; уже существовало: 0.")]
    public async Task ParticipantImportMessagesUseRequestCulture(string culture, string error, string status)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile",
            "First\nfirst"u8.ToArray(), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile",
            "First\nSecond"u8.ToArray(), "names.csv");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(status, WebUtility.HtmlDecode(await client.GetStringAsync(path, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("en", "Start time must be before end time.", "The file is not a PDF.", "Settings saved.")]
    [InlineData("ru", "Начало должно быть раньше окончания.", "Файл не является PDF.", "Настройки сохранены.")]
    public async Task EditingMessagesUseRequestCulture(string culture, string timeError, string pdfError, string status)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var path = $"/Contests/Edit/{contest.Id}";
        var invalid = Settings(contest.Abbr);
        invalid["Input.EndTime"] = "2030-01-01T09:00";
        var response = await SiteFixture.PostAsync(client, path, invalid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(timeError, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadPdf", "PdfFile", "not a PDF"u8.ToArray(), "statements.pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(pdfError, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        response = await SiteFixture.PostAsync(client, path, Settings(contest.Abbr));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(status, WebUtility.HtmlDecode(await client.GetStringAsync(path, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("en", "A/problem.json: file not found.")]
    [InlineData("ru", "A/problem.json: файл не найден.")]
    public async Task ArchiveErrorsFormatPathsUsingRequestCulture(string culture, string error)
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var zip = ProblemArchives.Zip(("A/input1.txt", [1]), ("A/output1.txt", [2]));
        var response = await SiteFixture.UploadAsync(client, $"/Contests/Edit/{contest.Id}?handler=ImportProblems",
            "ProblemsFile", zip, "problems.zip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(error, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CreateUsesPrefixedFormAndRefreshesOwnerCookie()
    {
        var user = await site.AddUserAsync(RoleNames.Jury);
        using var client = await site.LoginAsync(user);
        var html = await client.GetStringAsync("/Contests/Edit", TestContext.Current.CancellationToken);
        Assert.Contains("name=\"Input.Name\"", html);
        Assert.Contains("type=\"datetime-local\"", html);
        Assert.DoesNotContain("name=\"ProblemsFile\"", html);
        var abbr = "school" + Guid.NewGuid().ToString("N");
        var invalid = Settings(abbr);
        invalid["Input.EndTime"] = "2030-01-01T09:00";
        var invalidResponse = await SiteFixture.PostAsync(client, "/Contests/Edit", invalid);
        Assert.Equal(HttpStatusCode.OK, invalidResponse.StatusCode);
        Assert.DoesNotContain("name=\"ProblemsFile\"", await invalidResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var response = await SiteFixture.PostAsync(client, "/Contests/Edit", Settings(abbr));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/contests/edit/", location);
        // Uses a real Identity cookie obtained before the claim existed.
        Assert.Contains("name=\"ProblemsFile\"", await client.GetStringAsync(location, TestContext.Current.CancellationToken));
        var list = await client.GetStringAsync("/Contests", TestContext.Current.CancellationToken);
        Assert.Contains($"href=\"{location}\"", list);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = await db.Contests.SingleAsync(x => x.Abbr == abbr, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(contest.IsHidden);
        Assert.True(await db.UserClaims.AnyAsync(x => x.UserId == user.Id && x.ClaimType == ClaimNames.ContestManage && x.ClaimValue == contest.Id.ToString(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyAdminAndClaimedJuryCanManageIncludingDirectPosts()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var other = await site.AddUserAsync(RoleNames.Jury);
        using var otherClient = await site.LoginAsync(other);
        var edit = $"/Contests/Edit/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await otherClient.GetAsync(edit, TestContext.Current.CancellationToken)).StatusCode);
        var denied = await SiteFixture.PostAsync(otherClient, edit + "?handler=Unfreeze", [], "/Contests/Edit");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());
        using var scope = site.Services.CreateScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.SingleAsync(x => x.Id == contest.Id, cancellationToken: TestContext.Current.CancellationToken)).FreezeTime);
        using var ownerClient = await site.LoginAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync(edit, TestContext.Current.CancellationToken)).StatusCode);
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var adminClient = await site.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync(edit, TestContext.Current.CancellationToken)).StatusCode);
        var ordinary = await site.AddUserAsync();
        using var ordinaryClient = await site.LoginAsync(ordinary);
        Assert.Contains("AccessDenied", (await ordinaryClient.GetAsync("/Contests/Edit", TestContext.Current.CancellationToken)).Headers.Location!.ToString());
        // A claim without Jury does not grant management rights.
        Assert.False(Olymp.Site.Services.Authorization.ContestAccess.CanManage(
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimNames.ContestManage, contest.Id.ToString())], "test")), contest.Id));
    }

    [Fact]
    public async Task FormsValidateTimesUploadPdfAndUnfreeze()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Edit/{contest.Id}";
        var invalid = Settings(contest.Abbr);
        invalid["Input.EndTime"] = "2030-01-01T09:00";
        var response = await SiteFixture.PostAsync(client, path, invalid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Начало должно быть раньше окончания", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        var valid = Settings(contest.Abbr);
        valid["Input.Description"] = "<p>Описание</p>";
        valid["Input.IsHidden"] = "false";
        valid["Input.IsOpen"] = "true";
        valid["Input.FreezeTime"] = "2030-01-01T14:00";
        valid["Input.ProblemShowTime"] = "2030-01-01T09:30";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path, valid)).StatusCode);
        using (var settingsScope = site.Services.CreateScope())
        {
            var settings = await settingsScope.ServiceProvider.GetRequiredService<OlympContext>().Contests.SingleAsync(x => x.Id == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Новый контест", settings.Name);
            Assert.Equal("<p>Описание</p>", settings.Description);
            Assert.False(settings.IsHidden);
            Assert.True(settings.IsOpen);
            Assert.Equal(new DateTime(2030, 1, 1, 10, 0, 0), settings.StartTime.LocalDateTime);
            Assert.Equal(new DateTime(2030, 1, 1, 15, 0, 0), settings.EndTime.LocalDateTime);
            Assert.Equal(new DateTime(2030, 1, 1, 14, 0, 0), settings.FreezeTime!.Value.LocalDateTime);
            Assert.Equal(new DateTime(2030, 1, 1, 9, 30, 0), settings.ProblemShowTime!.Value.LocalDateTime);
        }
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadPdf", "PdfFile", "%PDF-1.4\ntest"u8.ToArray(), "statements.pdf");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        response = await client.GetAsync($"/Contests/DownloadProblems/{contest.Id}", TestContext.Current.CancellationToken);
        Assert.Equal("%PDF-1.4\ntest"u8.ToArray(), await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        int blobId;
        using (var before = site.Services.CreateScope())
            blobId = (await before.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(x => x.Id == contest.Id).Select(x => x.ProblemPdfId).SingleAsync(cancellationToken: TestContext.Current.CancellationToken))!.Value;
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=Unfreeze", [])).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=RemovePdf", [])).StatusCode);
        using var scope = site.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.SingleAsync(x => x.Id == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(saved.FreezeTime);
        Assert.Null(saved.ProblemPdfId);
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Blobs.AnyAsync(x => x.Id == blobId, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportPreservesProblemIdentitySubmissionsOtherProblemsAndBuildsManagerArchive()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Edit/{contest.Id}";
        var response = await SiteFixture.UploadAsync(client, path + "?handler=ImportProblems", "ProblemsFile", ProblemArchives.ProblemZip(), "problems.zip");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        int id;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var problem = await db.Problems.Include(x => x.Tests).SingleAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            id = problem.Id;
            Assert.Equal([0, 100], problem.Tests.OrderBy(x => x.Number).Select(x => x.Score));
            Assert.True(problem.Tests.Single(x => x.Number == 1).IsOpen);
            await ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip(2, "Другая"));
            await AddSubmissionsAsync(db, owner.Id, problem, [2]);
        }
        response = await SiteFixture.UploadAsync(client, path + "?handler=ImportProblems", "ProblemsFile",
            ProblemArchives.ProblemZip(name: "Новое имя", openTestCount: 0, scores: new() { [1] = 30, [2] = 70 }), "problems.zip");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var problem = await db.Problems.Include(x => x.Tests).SingleAsync(x => x.Id == id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Новое имя", problem.Name);
            Assert.Equal(2, await db.Problems.CountAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Submissions.CountAsync(x => x.ProblemId == id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal([30, 70], problem.Tests.OrderBy(x => x.Number).Select(x => x.Score));
            // Inactive tests must not enter the downloadable archive.
            await db.Tests.Where(x => x.ProblemId == id && x.Number == 2).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=GenerateTests", [])).StatusCode);
        response = await client.GetAsync($"/Contests/DownloadTests/{contest.Id}", TestContext.Current.CancellationToken);
        using var memory = new MemoryStream(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        using var zip = new ZipArchive(memory);
        var entry = zip.GetEntry($"Test{contest.Abbr}/1. Новое имя/input01.txt");
        Assert.NotNull(entry);
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        await input.CopyToAsync(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, bytes.ToArray());
        Assert.Null(zip.GetEntry($"Test{contest.Abbr}/1. Новое имя/input02.txt"));
    }

    [Fact]
    public async Task CsvIsIdempotentAndExistingLoginButtonsProduceUsableAccounts()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        foreach (var text in new[] { "\uFEFFИванов Иван\nПетров Пётр\n", " иВаНоВ иВаН \nПетров Пётр\nСидорова Анна\n" })
        {
            var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile", Encoding.UTF8.GetBytes(text), "names.csv");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        var invalid = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile", Encoding.UTF8.GetBytes("Новый\nПовтор\n повтор \n"), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.Equal(3, await db.Competitors.CountAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(await db.Competitors.Where(x => x.ContestId == contest.Id).AllAsync(x => x.IsApproved && x.UserName == null, cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=CreateRandomLogins", [])).StatusCode);
        Competitor competitor;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            competitor = await db.Competitors.FirstAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken);
            Assert.StartsWith("olymp", competitor.UserName);
            Assert.True(await db.UserClaims.AnyAsync(x => x.UserId == competitor.Id && x.ClaimType == ClaimNames.CompetitorContest, cancellationToken: TestContext.Current.CancellationToken));
            await db.Contests.Where(x => x.Id == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false), cancellationToken: TestContext.Current.CancellationToken);
        }
        using var participantClient = await site.LoginAsync(competitor, competitor.PasswordHash!);
        Assert.Equal(HttpStatusCode.OK, (await participantClient.GetAsync($"/Contests/Submits/{contest.Id}", TestContext.Current.CancellationToken)).StatusCode);
        var previous = competitor.UserName;
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, path + "?handler=CreateRandomLogins", [])).StatusCode);
        using (var scope = site.Services.CreateScope())
            Assert.Equal(previous, (await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.SingleAsync(x => x.Id == competitor.Id, cancellationToken: TestContext.Current.CancellationToken)).UserName);
    }

    [Fact]
    public async Task ParticipantPostsCannotModifyAnotherContestAndTeamImportIsRejected()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        var other = await site.AddContestAsync();
        int victim;
        using (var scope = site.Services.CreateScope())
        {
            var competitor = new Competitor
            {
                ContestId = other.Id,
                Contest = null!,
                Name = "Другой",
                IsApproved = true,
                Members = [],
                Messages = [],
                Submissions = []
            };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(competitor)).Succeeded);
            victim = competitor.Id;
        }
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        var response = await SiteFixture.PostAsync(client, path, new() { ["userId"] = victim.ToString(), ["action"] = "Disqualify" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var teamContest = await site.AddContestAsync(owner, "acm");
        // Log in again to pick up the newly granted claim.
        using var teamClient = await site.LoginAsync(owner);
        response = await SiteFixture.UploadAsync(teamClient, $"/Contests/Competitors/Manage/{teamContest.Id}?handler=UploadParticipants",
            "ParticipantsFile", Encoding.UTF8.GetBytes("Иванов"), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var finalScope = site.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False((await db.Competitors.SingleAsync(x => x.Id == victim, cancellationToken: TestContext.Current.CancellationToken)).IsDisqualified);
        Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == teamContest.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    internal static async Task ImportAsync(IServiceProvider provider, int contestId, byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        await provider.GetRequiredService<ContestProblemService>().ImportAsync(contestId, stream);
    }

    internal static async Task AddSubmissionsAsync(OlympContext db, int userId, Problem problem, int[] statuses)
    {
        var compilator = new Compilator
        {
            Name = "test",
            Description = "Test compiler",
            Language = "C",
            SourceExtension = ".c",
            CommandLine = "compile",
            ConfigName = "test",
            IsActive = true,
            Submissions = []
        };
        db.Compilators.Add(compilator);
        foreach (var status in statuses)
            db.Submissions.Add(new Submission
            {
                ProblemId = problem.Id,
                Problem = null!,
                UserId = userId,
                User = null!,
                Compilator = compilator,
                SourceCode = "test"u8.ToArray(),
                StatusCode = status,
                Score = status == 2 ? 100 : 25,
                ScoresByTest = "25;0",
                CommitTime = DateTimeOffset.Now,
                LastModification = DateTimeOffset.Now,
                CheckResults = [new CheckResult { Submission = null!, StatusCode = status, CheckTime = DateTimeOffset.Now, Log = "Old check" }]
            });
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, string> Settings(string abbr) => new()
    {
        ["Input.Name"] = "Новый контест",
        ["Input.Abbr"] = abbr,
        ["Input.StartTime"] = "2030-01-01T10:00",
        ["Input.EndTime"] = "2030-01-01T15:00",
        ["Input.IsHidden"] = "true",
        ["Input.IsOpen"] = "false"
    };
}
