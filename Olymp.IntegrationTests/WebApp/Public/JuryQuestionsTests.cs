using System.Net;
using System.Security.Claims;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.Shared;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

[Trait("Category", "Integration")]
public class JuryQuestionsTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task FormCanRenderWhileMessageHistoryIsStillLoading()
    {
        var contest = await AddContestAsync();
        var participant = await AddParticipantAsync(contest);
        var loading = new AsyncQueryInterceptor("[Messages]");
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<OlympContext>(options => options.AddInterceptors(loading))));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = participant.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var response = await WebAppFactory.PostFormAsync(client, Path(contest), "jury-question", new()
        {
            ["Input.Text"] = "A question after asynchronous loading",
            ["Input.ProblemId"] = ""
        });
        Assert.True(loading.WasDelayed);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("en", true)]
    public async Task ParticipantCanSendGeneralOrProblemQuestionAsTheSignedInAuthor(string culture, bool problemQuestion)
    {
        var contest = await AddContestAsync();
        var participant = await AddParticipantAsync(contest);
        using var client = await factory.LoginAsync(participant.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        string text = "Is this condition correct? <script>alert(1)</script>";
        int? problemId = problemQuestion ? contest.Problems.First().Id : null;
        using var response = await WebAppFactory.PostFormAsync(client, Path(contest), "jury-question", new()
        {
            ["Input.ProblemId"] = problemId?.ToString() ?? "",
            ["Input.Text"] = text,
            ["Input.UserId"] = "1",
            ["Input.ContestId"] = "1",
            ["Input.JuryText"] = "FORGED REPLY"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var message = await db.Messages.SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal(participant.Id, message.UserId);
        Assert.Equal(problemId, message.ProblemId);
        Assert.Equal(text, message.UserText);
        Assert.Null(message.JuryText);
        Assert.NotNull(message.SendTime);
        Assert.Equal($"{Path(contest)}#question-{message.Id}",
            new Uri(client.BaseAddress!, response.Headers.Location!).PathAndQuery + new Uri(client.BaseAddress!, response.Headers.Location!).Fragment);

        using var document = await GetAsync(client, Path(contest));
        Assert.Equal($"{(culture == "ru" ? "Вопросы" : "Questions")} – {contest.Name}", document.Title);
        Assert.Equal($"contests/{contest.Id}/questions", document.QuerySelector(".contest-header a[aria-current='page']")!.GetAttribute("href"));
        Assert.Equal(culture == "ru" ? "Тема" : "Topic", document.QuerySelector("label[for='question-topic']")!.TextContent);
        Assert.Equal(culture == "ru" ? "Вопрос" : "Question", document.QuerySelector("label[for='question-text']")!.TextContent);
        var article = document.QuerySelector($"#question-{message.Id}")!;
        Assert.Equal(text, article.QuerySelector("pre")!.TextContent);
        Assert.Null(article.QuerySelector("script"));
        Assert.Contains(culture == "ru" ? "Ожидает ответа" : "Awaiting reply", article.TextContent);
        Assert.Empty(document.QuerySelector("#question-text")!.TextContent);
    }

    [Fact]
    public async Task PrivateHistoryIncludesOnlyOwnMessagesForVisibleProblemsAndRefreshesReplies()
    {
        var contest = await AddContestAsync();
        var foreign = await AddContestAsync();
        var participant = await AddParticipantAsync(contest);
        var other = await AddParticipantAsync(contest);
        var now = DateTimeOffset.UtcNow;
        var general = Message(contest, participant.Id, "Own general", now.AddMinutes(-2));
        var task = Message(contest, participant.Id, "Own task", now.AddMinutes(-1), contest.Problems.First());
        task.JuryText = "<strong>Published private reply</strong>";
        var tie = Message(contest, participant.Id, "Own latest", now.AddMinutes(-1));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            db.Messages.AddRange(general, task, tie,
                Message(contest, other.Id, "OTHER PRIVATE", now),
                Message(contest, null, "PUBLIC QUESTION", now),
                Message(contest, participant.Id, "INACTIVE QUESTION", now, contest.Problems.Last()),
                Message(foreign, participant.Id, "OTHER CONTEST", now),
                Message(contest, participant.Id, "MISMATCHED PROBLEM", now, foreign.Problems.First()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(participant.UserName!);
        using var document = await GetAsync(client, Path(contest));
        Assert.Equal<string>([$"question-{tie.Id}", $"question-{task.Id}", $"question-{general.Id}"],
            document.QuerySelectorAll("main article.card").Select(item => item.Id!));
        Assert.NotNull(document.QuerySelector($"#question-{task.Id} strong"));
        Assert.NotNull(document.QuerySelector($"#question-{task.Id} time[datetime]"));
        foreach (string secret in new[] { "OTHER PRIVATE", "PUBLIC QUESTION", "INACTIVE QUESTION", "OTHER CONTEST", "MISMATCHED PROBLEM" })
            Assert.DoesNotContain(secret, document.QuerySelector("main")!.TextContent);
        Assert.Equal<string?>(["", contest.Problems.First().Id.ToString(), contest.Problems.Skip(1).First().Id.ToString()],
            document.QuerySelectorAll("#question-topic option").Select(item => item.GetAttribute("value")));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Messages.Where(item => item.Id == general.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.JuryText, "<em>New reply</em>"), TestContext.Current.CancellationToken);
        }
        using var refreshed = await GetAsync(client, document.QuerySelector("a.btn-outline-secondary")!.GetAttribute("href")!);
        Assert.Equal("New reply", refreshed.QuerySelector($"#question-{general.Id} em")!.TextContent);
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("permanent")]
    [InlineData("foreign")]
    [InlineData("upcoming")]
    [InlineData("ended")]
    [InlineData("hidden")]
    public async Task UnavailableCorrespondenceCannotBeRead(string state)
    {
        var contest = await AddContestAsync(state);
        var participant = await AddParticipantAsync(contest);
        var permanent = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await db.Competitors.Include(item => item.Members)
                .SingleAsync(item => item.Id == participant.Id, TestContext.Current.CancellationToken);
            stored.CoachId = permanent.Id;
            stored.Members.Add(await db.OlympUsers.FindAsync([permanent.Id], TestContext.Current.CancellationToken) ?? throw new InvalidOperationException());
            db.Messages.Add(Message(contest, participant.Id, "PRIVATE SECRET", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var viewer = state == "foreign" ? await AddParticipantAsync(await AddContestAsync()) : participant;
        using var client = state == "guest" ? factory.CreateSiteClient()
            : await factory.LoginAsync(state == "permanent" ? permanent.UserName! : viewer.UserName!);
        using var response = await client.GetAsync(Path(contest), TestContext.Current.CancellationToken);
        Assert.Equal(state == "guest" ? HttpStatusCode.Redirect : HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("PRIVATE SECRET", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("disqualified")]
    [InlineData("unapproved")]
    [InlineData("ended")]
    [InlineData("hidden")]
    [InlineData("foreign")]
    public async Task PostRechecksAccessAfterFormWasOpened(string change)
    {
        var contest = await AddContestAsync();
        var foreign = await AddContestAsync();
        var participant = await AddParticipantAsync(contest);
        using var client = await factory.LoginAsync(participant.UserName!);
        using var document = await GetAsync(client, Path(contest));
        var fields = document.QuerySelectorAll("form input[type='hidden'][name]")
            .Where(input => input.Closest("form")!.QuerySelector("#question-text") is not null)
            .ToDictionary(input => input.GetAttribute("name")!, input => input.GetAttribute("value")!);
        fields["Input.Text"] = "This must never be saved";
        fields["Input.ProblemId"] = "";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await db.Competitors.FindAsync([participant.Id], TestContext.Current.CancellationToken);
            var storedContest = await db.Contests.FindAsync([contest.Id], TestContext.Current.CancellationToken);
            switch (change)
            {
                case "disqualified": stored!.IsDisqualified = true; break;
                case "unapproved": stored!.IsApproved = false; break;
                case "ended": storedContest!.EndTime = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "hidden": storedContest!.IsHidden = true; break;
                case "foreign": stored!.ContestId = foreign.Id; break;
            }
            db.Messages.Add(Message(contest, participant.Id, "Existing private question", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(Path(contest), content, TestContext.Current.CancellationToken);
        Assert.False(response.IsSuccessStatusCode);
        using var checkScope = factory.Services.CreateScope();
        Assert.False(await checkScope.ServiceProvider.GetRequiredService<OlympContext>().Messages
            .AnyAsync(item => item.UserText == fields["Input.Text"], TestContext.Current.CancellationToken));
        if (change is "disqualified" or "unapproved")
        {
            using var history = await GetAsync(client, Path(contest));
            Assert.Contains("Existing private question", history.QuerySelector("main")!.TextContent);
            Assert.Null(history.QuerySelector("#question-text"));
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("whitespace")]
    [InlineData("inactive")]
    [InlineData("foreign")]
    public async Task InvalidQuestionsAreNotStoredAndInputIsPreserved(string invalid)
    {
        var contest = await AddContestAsync();
        var foreign = await AddContestAsync();
        var participant = await AddParticipantAsync(contest);
        using var client = await factory.LoginAsync(participant.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
        string text = invalid switch
        {
            "empty" => "",
            "short" => "Short",
            "long" => new string('x', 4097),
            "whitespace" => new string(' ', 15),
            _ => "A valid question about a problem"
        };
        string problemId = invalid switch
        {
            "inactive" => contest.Problems.Last().Id.ToString(),
            "foreign" => foreign.Problems.First().Id.ToString(),
            _ => ""
        };
        using var response = await WebAppFactory.PostFormAsync(client, Path(contest), "jury-question", new()
        {
            ["Input.Text"] = text,
            ["Input.ProblemId"] = problemId
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains(document.QuerySelectorAll("main [role='alert']"), item => !string.IsNullOrWhiteSpace(item.TextContent));
        Assert.Equal(text, document.QuerySelector("#question-text")!.TextContent);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Messages
            .AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrossSitePostIsRejected()
    {
        var contest = await AddContestAsync();
        var participant = await AddParticipantAsync(contest);
        using var client = await factory.LoginAsync(participant.UserName!);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "jury-question",
            ["Input.Text"] = "A cross-site question"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(contest)) { Content = content };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://other.example.test");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Messages
            .AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    private async Task<Contest> AddContestAsync(string state = "running")
    {
        var now = DateTimeOffset.UtcNow;
        var contest = TestEntities.CreateContest($"Questions {Guid.NewGuid():N}",
            now.AddHours(state == "upcoming" ? 1 : -2), now.AddHours(state == "ended" ? -1 : 2));
        contest.IsHidden = state == "hidden";
        contest.Problems.Add(TestEntities.CreateProblem(contest, 1));
        contest.Problems.Add(TestEntities.CreateProblem(contest, 2));
        var inactive = TestEntities.CreateProblem(contest, 3);
        inactive.IsActive = false;
        contest.Problems.Add(inactive);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Contests.Add(contest);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return contest;
    }

    private async Task<Competitor> AddParticipantAsync(Contest contest)
    {
        var participant = TestEntities.CreateCompetitor(contest, $"question_{Guid.NewGuid():N}");
        participant.UserName = participant.Name;
        participant.Contest = null!;
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(participant, WebAppFactory.Password)).Succeeded);
        Assert.True((await users.AddClaimAsync(participant,
            new Claim(ClaimNames.CompetitorContest, contest.Id.ToString()))).Succeeded);
        return participant;
    }

    private static Message Message(Contest contest, int? userId, string text, DateTimeOffset sent, Problem? problem = null) => new()
    {
        Contest = null!,
        ContestId = contest.Id,
        UserId = userId,
        UserText = text,
        SendTime = sent,
        ProblemId = problem?.Id
    };

    private static string Path(Contest contest) => $"/contests/{contest.Id}/questions";

    private static async Task<IDocument> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
    }
}
