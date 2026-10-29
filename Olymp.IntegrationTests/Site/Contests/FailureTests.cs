using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.Services.Contests;
using Olymp.Tests.Shared;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class FailureTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Fact]
    public async Task ProductionHostSupportsManagementAndRejectsOversizedUploads()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        using var production = site.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ContestImport:MaxZipBytes"] = "1024",
                    ["ContestImport:MaxPdfBytes"] = "16"
                }));
        });
        using var client = production.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(Environments.Production, production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = owner.UserName!,
            ["Input.Password"] = SiteFixture.Password
        })).StatusCode);
        var response = await SiteFixture.PostAsync(client, "/Contests/Edit", new()
        {
            ["Input.Name"] = "Production check",
            ["Input.Abbr"] = "school" + Guid.NewGuid().ToString("N"),
            ["Input.StartTime"] = "2030-01-01T10:00",
            ["Input.EndTime"] = "2030-01-01T15:00"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var path = response.Headers.Location!.ToString();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path + "?handler=Unfreeze", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.UploadAsync(client, path + "?handler=ImportProblems",
            "ProblemsFile", ProblemArchives.ProblemZip(), "problems.zip")).StatusCode);
        var pdf = "%PDF-1.4\ntest"u8.ToArray();
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.UploadAsync(client, path + "?handler=UploadPdf",
            "PdfFile", pdf, "statements.pdf")).StatusCode);
        foreach (var (handler, field, bytes, filename) in new[]
        {
            ("ImportProblems", "ProblemsFile", new byte[1025], "problems.zip"),
            ("UploadPdf", "PdfFile", new byte[17], "statements.pdf"),
            ("ImportProblems", "ProblemsFile", [], "problems.zip"),
            ("UploadPdf", "PdfFile", [], "statements.pdf")
        })
        {
            response = await SiteFixture.UploadAsync(client, path + "?handler=" + handler, field, bytes, filename);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Недопустимый размер файла", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        }
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contestId = int.Parse(path.Split('/').Last());
        Assert.Equal(1, await db.Problems.CountAsync(x => x.ContestId == contestId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(pdf, await db.Contests.Where(x => x.Id == contestId).Select(x => x.ProblemPdf!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedOwnerClaimRollsBackContestAndFailedParticipantRollsBackWholeCsv()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var failing = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<UserManager<User>, FailingUserManager>()));
        using var client = failing.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.PostAsync(client, "/Identity/Account/Login", new()
        {
            ["Input.Username"] = owner.UserName!,
            ["Input.Password"] = SiteFixture.Password
        })).StatusCode);
        var abbr = "failure" + Guid.NewGuid().ToString("N");
        var response = await SiteFixture.PostAsync(client, "/Contests/Edit", new()
        {
            ["Input.Name"] = "Не сохранять",
            ["Input.Abbr"] = abbr,
            ["Input.StartTime"] = "2030-01-01T10:00",
            ["Input.EndTime"] = "2030-01-01T15:00"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("name=\"ProblemsFile\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        response = await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants", "ParticipantsFile",
            Encoding.UTF8.GetBytes("Сохранён первым\nСбой\n"), "names.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await db.Contests.AnyAsync(x => x.Abbr == abbr, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Competitors.AnyAsync(x => x.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportedParticipantsReceiveUploadedLoginsAndAdminCanViewTheirCards()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var client = await site.LoginAsync(owner);
        var path = $"/Contests/Competitors/Manage/{contest.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await SiteFixture.UploadAsync(client, path + "?handler=UploadParticipants",
            "ParticipantsFile", "Первый\nВторой"u8.ToArray(), "names.csv")).StatusCode);
        var free = "free" + Guid.NewGuid().ToString("N");
        int competitorId;
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.True(await db.Competitors.Where(x => x.ContestId == contest.Id).AllAsync(x => x.UserName == null, cancellationToken: TestContext.Current.CancellationToken));
            competitorId = await db.Competitors.Where(x => x.ContestId == contest.Id).Select(x => x.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        var response = await SiteFixture.UploadAsync(client, path + "?handler=UploadLogins", "LoginsFile",
            Encoding.UTF8.GetBytes($"{free};12345678\n{free}b;23456789"), "logins.csv");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var admin = await site.AddUserAsync(RoleNames.Admin);
        using var adminClient = await site.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"/Users/Manage/{competitorId}", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task EmptyGenerationAndInvalidImportKeepPreviousArchiveAndProblem()
    {
        var owner = await site.AddUserAsync(RoleNames.Jury);
        var contest = await site.AddContestAsync(owner);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, ProblemArchives.ProblemZip());
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await service.GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken);
        var old = await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.TestZip!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var invalid = ProblemArchives.Zip(
            ("A/problem.json", "{\"number\":1,\"name\":\"Не сохранять\",\"openTestCount\":0}"u8.ToArray()),
            ("A/input1.txt", [1]), ("A/output1.txt", [2]),
            ("B/problem.json", "{\"number\":2,\"name\":\"Ошибка\",\"openTestCount\":0,\"checkerId\":999}"u8.ToArray()),
            ("B/input1.txt", [3]), ("B/output1.txt", [4]));
        await Assert.ThrowsAsync<InvalidDataException>(() => ContestManagementTests.ImportAsync(scope.ServiceProvider, contest.Id, invalid));
        db.ChangeTracker.Clear();
        Assert.Equal("Задача", await db.Problems.Where(x => x.ContestId == contest.Id).Select(x => x.Name).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.Tests.CountAsync(x => x.Problem.ContestId == contest.Id, cancellationToken: TestContext.Current.CancellationToken));
        await db.Problems.Where(x => x.ContestId == contest.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateTestZipAsync(contest.Id, TestContext.Current.CancellationToken));
        db.ChangeTracker.Clear();
        Assert.Equal(old, await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.TestZip!.Data).SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class FailingUserManager(IUserStore<User> store, IOptions<IdentityOptions> options,
        IPasswordHasher<User> hasher, IEnumerable<IUserValidator<User>> userValidators,
        IEnumerable<IPasswordValidator<User>> passwordValidators, ILookupNormalizer normalizer,
        IdentityErrorDescriber errors, IServiceProvider services, ILogger<UserManager<User>> logger)
        : UserManager<User>(store, options, hasher, userValidators, passwordValidators, normalizer, errors, services, logger)
    {
        public override Task<IdentityResult> AddClaimAsync(User user, Claim claim) => claim.Type == ClaimNames.ContestManage
            ? Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Injected claim failure" }))
            : base.AddClaimAsync(user, claim);

        public override Task<IdentityResult> CreateAsync(User user) => user.Name == "Сбой"
            ? Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Injected participant failure" }))
            : base.CreateAsync(user);
    }
}
