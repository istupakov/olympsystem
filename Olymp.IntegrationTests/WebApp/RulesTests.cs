using System.Net;

using AngleSharp.Html.Parser;

using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;

using Xunit;

namespace Olymp.IntegrationTests.WebApp;

[Trait("Category", "Integration")]
public class RulesTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData("ru", "acm", true, true, false)]
    [InlineData("en", "acm", true, true, false)]
    [InlineData("ru", "nstu", false, true, false)]
    [InlineData("en", "nstu", false, true, false)]
    [InlineData("ru", "school", false, false, false)]
    [InlineData("en", "school", false, false, false)]
    [InlineData("ru", "acm", true, true, true)]
    [InlineData("en", "acm", true, true, true)]
    [InlineData("ru", "nstu", false, true, true)]
    [InlineData("en", "nstu", false, true, true)]
    [InlineData("ru", "school", false, false, true)]
    [InlineData("en", "school", false, false, true)]
    public async Task ContestRulesMatchContestFormat(string culture, string abbreviation, bool team, bool scoring, bool ended)
    {
        var contest = CreateContest(abbreviation);
        if (ended)
        {
            contest.StartTime = DateTimeOffset.UtcNow.AddHours(-2);
            contest.EndTime = DateTimeOffset.UtcNow.AddHours(-1);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            context.Contests.Add(contest);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync($"/contests/{contest.Id}/rules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? $"Правила – {contest.Name}" : $"Rules – {contest.Name}", document.Title);
        string text = document.QuerySelector("main")!.TextContent;
        Assert.Equal(team, text.Contains("TRD", StringComparison.Ordinal));
        Assert.Equal(team, text.Contains(culture == "ru" ? "внутри зарегистрированного состава команды" : "within the registered team", StringComparison.Ordinal));
        Assert.Equal(scoring, document.QuerySelectorAll("h2").Any(heading => heading.TextContent == (culture == "ru" ? "Подсчёт результатов" : "Scoring")));
        Assert.Equal(scoring, text.Contains("60 + 2 × 20", StringComparison.Ordinal));
        Assert.DoesNotContain("Runtime error", text);
        Assert.NotNull(document.QuerySelector("a[href='rules']"));
        Assert.Equal(!ended, text.Contains(culture == "ru" ? "Вход для участия" : "Signing In to Participate", StringComparison.Ordinal));
        Assert.Equal(!ended && team, document.QuerySelector($"main a[href='contests/{contest.Id}/registration/teams']") is not null);
        Assert.Equal(!ended && !team, document.QuerySelector($"main a[href='contests/{contest.Id}/registration']") is not null);
        Assert.Equal(!ended && team, text.Contains(culture == "ru" ? "под аккаунтом команды" : "with the team account", StringComparison.Ordinal));
        Assert.Equal(!ended && !team, text.Contains(culture == "ru" ? "При участии вне конкурса" : "For out-of-competition participation", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownAndHiddenContestRulesReturnNotFound(bool hidden)
    {
        int id = int.MaxValue;
        if (hidden)
        {
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var contest = CreateContest("acm");
            contest.IsHidden = true;
            context.Contests.Add(contest);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = contest.Id;
        }

        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync($"/contests/{id}/rules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task SystemUsageRulesArePublicAndDoNotIncludeContestRequirements(string culture)
    {
        string version = $"Test compiler {Guid.NewGuid():N}";
        string hiddenVersion = $"Hidden compiler {Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            foreach (var active in new[] { true, false })
                context.Compilators.Add(new Compilator
                {
                    Language = "C++",
                    Name = "Test compiler",
                    Description = active ? version : hiddenVersion,
                    SourceExtension = ".cpp",
                    CommandLine = "private-command",
                    ConfigName = "test",
                    IsActive = active,
                    Submissions = []
                });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateSiteClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var systemResponse = await client.GetAsync("/rules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, systemResponse.StatusCode);
        using var system = await new HtmlParser().ParseDocumentAsync(
            await systemResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Правила использования системы" : "System Usage Rules", system.Title);
        Assert.NotNull(system.QuerySelector("#submissions"));
        Assert.NotNull(system.QuerySelector("#judging"));
        string systemText = system.QuerySelector("main")!.TextContent;
        Assert.Contains(version, systemText);
        Assert.DoesNotContain(hiddenVersion, systemText);
        Assert.DoesNotContain("private-command", systemText);
        Assert.Contains("Runtime error", systemText);
        Assert.DoesNotContain(culture == "ru" ? "использование ИИ" : "using AI", systemText);
        Assert.Null(system.QuerySelector("a[href='contests/rules']"));

        using var removedPage = await client.GetAsync("/contests/rules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, removedPage.StatusCode);
    }

    private static Contest CreateContest(string abbreviation) => new()
    {
        Name = "Rules test " + Guid.NewGuid().ToString("N"),
        Abbr = abbreviation + Guid.NewGuid().ToString("N"),
        StartTime = DateTimeOffset.UtcNow,
        EndTime = DateTimeOffset.UtcNow.AddHours(5),
        Messages = [],
        Problems = [],
        Competitors = []
    };
}
