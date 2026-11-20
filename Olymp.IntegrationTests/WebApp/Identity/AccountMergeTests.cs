using System.Globalization;
using System.Net;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Identity;

[Trait("Category", "Integration")]
public class AccountMergeTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task MergeTransfersHistoryAndCoachLinksWithoutChangingProfilePermissionsOrRosterNames()
    {
        var target = await factory.AddUserAsync();
        var source = await factory.AddUserAsync(legacyLogin: true);
        var coached = await factory.AddUserAsync();
        int participantId, submissionId, messageId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var storedSource = (OlympUser)(await users.FindByIdAsync(source.Id.ToString(CultureInfo.InvariantCulture)))!;
            storedSource.Name = "Different source";
            storedSource.FirstName = "Иван";
            storedSource.IsDisqualified = true;
            storedSource.IsHidden = true;
            Assert.True((await users.UpdateAsync(storedSource)).Succeeded);
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
            if (!await roles.RoleExistsAsync(RoleNames.Jury))
                Assert.True((await roles.CreateAsync(new IdentityRole<int>(RoleNames.Jury))).Succeeded);
            Assert.True((await users.AddToRoleAsync(storedSource, RoleNames.Jury)).Succeeded);
            Assert.True((await users.AddClaimAsync(storedSource, new Claim(ClaimNames.CoachOrg, "123"))).Succeeded);
            Assert.True((await users.AddLoginAsync(storedSource, new("test", Guid.NewGuid().ToString("N"), "Test"))).Succeeded);
            Assert.True((await users.SetAuthenticationTokenAsync(storedSource, "test", "name", "value")).Succeeded);
            var storedTarget = await db.OlympUsers.SingleAsync(item => item.Id == target.Id, TestContext.Current.CancellationToken);
            storedTarget.CoachId = source.Id;
            (await db.OlympUsers.SingleAsync(item => item.Id == coached.Id, TestContext.Current.CancellationToken)).CoachId = source.Id;
            var contest = TestEntities.CreateContest("Merge history", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            var participant = TestEntities.CreateCompetitor(contest, "Historical team");
            participant.CoachId = source.Id;
            participant.MemberInfos = $"Historical name;{source.Id};Another historical name;{target.Id}";
            participant.Members.Add(storedSource);
            participant.Members.Add(storedTarget);
            var problem = TestEntities.CreateProblem(contest);
            var submission = TestEntities.CreateSubmission(storedSource, problem, TestEntities.CreateCompilator(), DateTimeOffset.UtcNow.AddDays(-1), [1, 2, 3]);
            var message = new Message { User = storedSource, Contest = contest, UserText = "Historical question", JuryText = "Historical answer" };
            db.AddRange(participant, submission, message);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            participantId = participant.Id;
            submissionId = submission.Id;
            messageId = message.Id;
        }
        using var sourceClient = await factory.LoginAsync(source.UserName!);
        using var client = await factory.LoginAsync(target.UserName!);
        using var response = await MergeAsync(client, source.UserName!);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.False(await context.Users.AnyAsync(item => item.Id == source.Id, TestContext.Current.CancellationToken));
        var saved = await context.OlympUsers.SingleAsync(item => item.Id == target.Id, TestContext.Current.CancellationToken);
        Assert.Equal(target.Name, saved.Name);
        Assert.Null(saved.FirstName);
        Assert.Equal(target.Email, saved.Email);
        Assert.Equal(target.PasswordHash, saved.PasswordHash);
        Assert.Equal(target.SecurityStamp, saved.SecurityStamp);
        Assert.False(saved.IsHidden);
        Assert.False(saved.IsDisqualified);
        Assert.Null(saved.CoachId);
        Assert.Equal(target.Id, (await context.OlympUsers.SingleAsync(item => item.Id == coached.Id, TestContext.Current.CancellationToken)).CoachId);
        var team = await context.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == participantId, TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, Assert.Single(team.Members).Id);
        Assert.Equal(target.Id, team.CoachId);
        Assert.Equal($"Historical name;{target.Id};Another historical name;{target.Id}", team.MemberInfos);
        Assert.Equal(["Historical name", "Another historical name"], team.MemberNames);
        var transferred = await context.Submissions.SingleAsync(item => item.Id == submissionId, TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, transferred.UserId);
        Assert.Equal(new byte[] { 1, 2, 3 }, transferred.SourceCode);
        var question = await context.Messages.SingleAsync(item => item.Id == messageId, TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, question.UserId);
        Assert.Equal("Historical question", question.UserText);
        Assert.Equal("Historical answer", question.JuryText);
        Assert.False(await context.UserRoles.AnyAsync(item => item.UserId == target.Id || item.UserId == source.Id, TestContext.Current.CancellationToken));
        Assert.False(await context.UserClaims.AnyAsync(item => item.UserId == target.Id || item.UserId == source.Id, TestContext.Current.CancellationToken));
        Assert.False(await context.UserLogins.AnyAsync(item => item.UserId == target.Id || item.UserId == source.Id, TestContext.Current.CancellationToken));
        Assert.False(await context.UserTokens.AnyAsync(item => item.UserId == target.Id || item.UserId == source.Id, TestContext.Current.CancellationToken));
        using var oldLogin = await WebAppFactory.PostFormAsync(sourceClient, "/Account/Login", "login", new()
        {
            ["Input.Login"] = source.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        using var oldProfile = await sourceClient.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, oldProfile.StatusCode);
    }

    [Theory]
    [InlineData("ru", "обратитесь к администратору")]
    [InlineData("en", "Contact an administrator")]
    public async Task ConflictingParticipationsRejectMergeWithoutChanges(string culture, string expected)
    {
        var target = await factory.AddUserAsync();
        var source = await factory.AddUserAsync();
        int sourceParticipantId;
        string contestName = $"Conflict {Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var contest = TestEntities.CreateContest(contestName, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            var first = TestEntities.CreateCompetitor(contest, "First");
            var second = TestEntities.CreateCompetitor(contest, "Second");
            first.Members.Add(await db.OlympUsers.SingleAsync(item => item.Id == source.Id, TestContext.Current.CancellationToken));
            second.Members.Add(await db.OlympUsers.SingleAsync(item => item.Id == target.Id, TestContext.Current.CancellationToken));
            db.AddRange(first, second);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            sourceParticipantId = first.Id;
        }
        using var client = await factory.LoginAsync(target.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await MergeAsync(client, source.UserName!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        string error = document.QuerySelector(".alert-danger")!.TextContent;
        Assert.Contains(contestName, error);
        Assert.Contains(expected, error);
        Assert.DoesNotContain(WebAppFactory.Password, html);
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.True(await context.Users.AnyAsync(item => item.Id == source.Id, TestContext.Current.CancellationToken));
        var participant = await context.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == sourceParticipantId, TestContext.Current.CancellationToken);
        Assert.Equal(source.Id, Assert.Single(participant.Members).Id);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("self")]
    [InlineData("unconfirmed")]
    [InlineData("locked")]
    [InlineData("missing-confirmation")]
    [InlineData("temporary")]
    public async Task InvalidMergeDoesNotDeleteEitherAccount(string reason)
    {
        var target = await factory.AddUserAsync();
        var source = await factory.AddUserAsync(emailConfirmed: reason != "unconfirmed");
        string login = reason == "self" ? target.UserName! : source.UserName!;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (reason == "locked")
            {
                var user = await db.OlympUsers.SingleAsync(item => item.Id == source.Id, TestContext.Current.CancellationToken);
                user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            if (reason == "temporary")
            {
                var participant = TestEntities.CreateCompetitor(TestEntities.CreateContest("Temporary", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), "Temporary");
                participant.UserName = $"temp{Guid.NewGuid():N}";
                Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(participant, WebAppFactory.Password)).Succeeded);
                login = participant.UserName;
            }
        }
        using var client = await factory.LoginAsync(target.UserName!);
        using var response = await MergeAsync(client, login, reason == "wrong-password" ? "wrong" : WebAppFactory.Password, reason != "missing-confirmation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal(2, await context.OlympUsers.CountAsync(item => item.Id == source.Id || item.Id == target.Id, TestContext.Current.CancellationToken));
        if (reason == "wrong-password")
            Assert.Equal(1, (await context.OlympUsers.SingleAsync(item => item.Id == source.Id, TestContext.Current.CancellationToken)).AccessFailedCount);
    }

    [Fact]
    public async Task FailedIdentityDeletionRollsBackTransferredLinks()
    {
        var target = await factory.AddUserAsync();
        var source = await factory.AddUserAsync();
        var coached = await factory.AddUserAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            (await db.OlympUsers.SingleAsync(item => item.Id == coached.Id, TestContext.Current.CancellationToken)).CoachId = source.Id;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IUserStore<User>, RejectDeletionStore>()));
        using var client = site.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = target.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var response = await MergeAsync(client, source.UserName!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.True(await context.Users.AnyAsync(item => item.Id == source.Id, TestContext.Current.CancellationToken));
        Assert.Equal(source.Id, (await context.OlympUsers.SingleAsync(item => item.Id == coached.Id, TestContext.Current.CancellationToken)).CoachId);
    }

    private static Task<HttpResponseMessage> MergeAsync(HttpClient client, string login, string password = WebAppFactory.Password, bool confirm = true) =>
        WebAppFactory.PostFormAsync(client, "/Account/Manage/Merge", "merge", new()
        {
            ["Input.Login"] = login,
            ["Input.Password"] = password,
            ["Input.Confirm"] = confirm.ToString()
        });

    private sealed class RejectDeletionStore(OlympContext context)
        : Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserStore<User, IdentityRole<int>, OlympContext, int>(context)
    {
        public override Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "TestDeleteFailure", Description = "Test delete failure" }));
    }
}
