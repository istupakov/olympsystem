using System.Globalization;
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
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Public;

[Trait("Category", "Integration")]
public class TeamRegistrationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task TeamFormCanRenderWhileMembersAreStillLoading()
    {
        var (contest, organization, coach) = await SetupAsync();
        var loading = new AsyncQueryInterceptor("[AspNetUsers]");
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<OlympContext>(options => options.AddInterceptors(loading))));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var login = await WebAppFactory.PostFormAsync(client, "/Account/Login", "login", new()
        {
            ["Input.Login"] = coach.UserName!,
            ["Input.Password"] = WebAppFactory.Password
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var response = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", TeamFields());
        Assert.True(loading.WasDelayed);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task IcpcRegistrationHasNoOfficialOptionAndIgnoresForgedOfficialFlag(string culture)
    {
        var token = TestContext.Current.CancellationToken;
        var (contest, organization, coach) = await SetupAsync();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OlympContext>().Contests.Where(item => item.Id == contest.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Abbr, "icpc" + contest.Abbr), token);
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync(CreatePath(contest.Id, organization.Id), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        Assert.Null(document.QuerySelector("input[type='checkbox'][name='Input.OutOfCompetition']"));
        Assert.Equal("true", document.QuerySelector("input[type='hidden'][name='Input.OutOfCompetition']")!.GetAttribute("value"));
        Assert.Contains("icpc.global", document.QuerySelector("main")!.TextContent);
        using var created = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", TeamFields(outOfCompetition: false));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var check = factory.Services.CreateScope();
        var team = await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors.SingleAsync(item => item.ContestId == contest.Id, token);
        Assert.True(team.IsOutOfCompetition);
    }

    [Theory]
    [InlineData("ru", 3, false)]
    [InlineData("en", 3, false)]
    [InlineData("ru", 1, true)]
    [InlineData("en", 2, true)]
    [InlineData("ru", 3, true)]
    public async Task TeamCreationPreviewsAndPreservesMixedComposition(string culture, int size, bool outOfCompetition)
    {
        var (contest, organization, coach) = await SetupAsync();
        var member = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            stored.OrganizationId = organization.Id;
            stored.FirstName = "Иван";
            stored.LastName = "Иванов";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var fields = TeamFields(size, outOfCompetition);
        fields.Remove("Input.Member1Name");
        fields["Input.Member1Id"] = member.Id.ToString();
        fields["Input.Action"] = "preview";
        using var preview = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", new(fields));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? $"Регистрация команды – {contest.Name}" : $"Team registration – {contest.Name}", document.Title);
        Assert.Equal("NSTU 1: Test Team", document.QuerySelector("section p.fw-semibold > span:not(.badge)")!.TextContent);
        Assert.Equal(size, document.QuerySelectorAll("section li").Length);
        Assert.Equal(culture == "ru" ? "Фамилия и имя для соревнования" : "Last name and first name for the contest",
            document.QuerySelector("label[for='Input.Member1Name']")!.TextContent);
        Assert.Equal(culture == "ru" ? "Иванов Иван" : "Smith John",
            document.QuerySelector("input[name='Input.Member1Name']")!.GetAttribute("placeholder"));
        Assert.Contains(culture == "ru" ? "Аккаунт участника (рекомендуется)" : "Contestant account (recommended)",
            document.QuerySelector("label[for='Input.Member1Id']")!.TextContent);
        Assert.Equal(size - 1, document.QuerySelectorAll("section li").Count(item => item.TextContent.Contains(
            culture == "ru" ? "(аккаунт не привязан)" : "(account not linked)", StringComparison.Ordinal)));
        using (var scope = factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .AnyAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));

        fields["Input.Action"] = "register";
        using var created = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", fields);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.EndsWith($"/contests/{contest.Id}/registration/teams", created.Headers.Location!.OriginalString);
        using var check = factory.Services.CreateScope();
        var team = await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Include(item => item.Members)
            .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal("NSTU 1: Test Team", team.Name);
        Assert.Equal(coach.Id, team.CoachId);
        Assert.Equal(organization.Id, team.OrganizationId);
        Assert.Equal(outOfCompetition, team.IsOutOfCompetition);
        Assert.False(team.IsApproved);
        Assert.Null(team.UserName);
        Assert.Null(team.PasswordHash);
        Assert.Equal(member.Id, Assert.Single(team.Members).Id);
        string[] expectedNames = ["Иванов Иван", "Second Member", "Third Member"];
        Assert.Equal(expectedNames.Take(size), team.MemberNames);
        using var teamList = await client.GetAsync(ListPath(contest.Id), TestContext.Current.CancellationToken);
        using var listDocument = await new HtmlParser().ParseDocumentAsync(
            await teamList.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var teamRow = Assert.Single(listDocument.QuerySelectorAll("main tbody tr"));
        Assert.Equal("Иванов Иван", teamRow.QuerySelector($"a[href='mailto:{member.Email}']")!.TextContent);
        Assert.Equal(coach.Email, teamRow.QuerySelector($"a[href='mailto:{coach.Email}']")!.TextContent);
        Assert.Single(teamRow.QuerySelectorAll("td:nth-child(2) a"));
        using var publicPage = await client.GetAsync($"/contests/{contest.Id}/competitors", TestContext.Current.CancellationToken);
        string publicText = WebUtility.HtmlDecode(await publicPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Иванов Иван", publicText);
        Assert.DoesNotContain(member.Email!, publicText);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task OutOfCompetitionTeamMayUseAnyMemberSlots(int slots)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var client = await factory.LoginAsync(coach.UserName!);
        string path = CreatePath(contest.Id, organization.Id);
        var fields = TeamFields(outOfCompetition: true);
        string[] names = ["First Member", "Second Member", "Third Member"];
        var expected = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            if ((slots & (1 << i)) == 0)
                fields[$"Input.Member{i + 1}Name"] = "";
            else
                expected.Add(names[i]);
        }
        fields["Input.Action"] = "preview";
        using var preview = await WebAppFactory.PostFormAsync(client, path, "create-team", fields);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(expected.Count, document.QuerySelectorAll("section li").Length);
        var confirmation = ReadFormFields(document.QuerySelector("form:has(input[name='Input.TeamPrefix'])")!);
        confirmation["Input.Action"] = "register";
        confirmation["Input.Confirmation"] = "true";
        confirmation["Input.AcceptRules"] = "true";
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(confirmation), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var check = factory.Services.CreateScope();
        var team = await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal(expected, team.MemberNames);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task EmptyOutOfCompetitionTeamShowsCompositionError(string culture)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var fields = TeamFields(size: 0, outOfCompetition: true);
        fields["Input.Action"] = "preview";
        using var response = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains(culture == "ru" ? "Укажите хотя бы одного участника." : "Specify at least one team member.",
            document.QuerySelectorAll("[role=alert]:not([data-valmsg-summary])").Select(element => element.TextContent));
        Assert.Null(document.QuerySelector("#team-preview"));
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru", true, true)]
    [InlineData("en", false, true)]
    [InlineData("ru", false, false)]
    [InlineData("en", true, false)]
    public async Task LinkedAccountAndContestNameAreIndependent(string culture, bool completeProfile, bool manualName)
    {
        var (contest, organization, coach) = await SetupAsync();
        var member = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            stored.OrganizationId = organization.Id;
            stored.FirstName = completeProfile ? "Иван" : null;
            stored.LastName = completeProfile ? "Иванов" : null;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var page = await client.GetAsync(CreatePath(contest.Id, organization.Id), TestContext.Current.CancellationToken);
        using var form = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        string accountLabel = form.QuerySelector($"select[id='Input.Member1Id'] option[value='{member.Id}']")!.TextContent;
        Assert.Contains(completeProfile ? "Иванов Иван" : culture == "ru"
            ? "Имя или фамилия в профиле не заполнены" : "Profile name is incomplete", accountLabel);
        Assert.Contains(member.Email!, accountLabel);

        var fields = TeamFields();
        fields["Input.Member1Id"] = member.Id.ToString();
        fields["Input.Member1Name"] = manualName ? "  Петров Пётр  " : "";
        fields["Input.Action"] = "preview";
        using var preview = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", new(fields));
        using var document = await new HtmlParser().ParseDocumentAsync(
            await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        if (!completeProfile && !manualName)
        {
            Assert.Contains(culture == "ru"
                ? "Укажите фамилию и имя для соревнования: в профиле выбранного аккаунта они не заполнены."
                : "Enter the last name and first name for the contest: the selected account’s profile name is incomplete.",
                document.QuerySelectorAll(".validation-message").Select(element => element.TextContent));
            using var missing = factory.Services.CreateScope();
            Assert.False(await missing.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .AnyAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
            return;
        }
        var first = document.QuerySelector("section li")!;
        Assert.Contains(manualName ? "Петров Пётр" : "Иванов Иван", first.TextContent);
        Assert.Contains(member.Email!, first.TextContent);
        fields["Input.Action"] = "register";
        using var created = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", fields);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OlympContext>();
        var team = await db.Competitors.Include(item => item.Members)
            .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal(manualName ? "Петров Пётр" : "Иванов Иван", team.MemberNames!.First());
        Assert.Equal(member.Id, Assert.Single(team.Members).Id);
        var profile = await db.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
        Assert.Equal(completeProfile ? "Иван" : null, profile.FirstName);
        Assert.Equal(completeProfile ? "Иванов" : null, profile.LastName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationPreservesPreviewedProfileName(bool longName)
    {
        var (contest, organization, coach) = await SetupAsync();
        var member = await factory.AddUserAsync();
        string lastName = longName ? new string('И', 64) : "Иванов";
        string firstName = longName ? new string('А', 64) : "Иван";
        string expectedName = $"{lastName} {firstName}";
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            stored.OrganizationId = organization.Id;
            stored.FirstName = firstName;
            stored.LastName = lastName;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        string path = CreatePath(contest.Id, organization.Id);
        var fields = TeamFields();
        fields["Input.Member1Id"] = member.Id.ToString();
        fields["Input.Member1Name"] = "";
        fields["Input.Action"] = "preview";
        using var preview = await WebAppFactory.PostFormAsync(client, path, "create-team", fields);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var form = document.QuerySelector("form:has(input[name='Input.TeamPrefix'])")!;
        Assert.Equal(expectedName, form.QuerySelector("input[name='Input.Member1Name'][readonly]")!.GetAttribute("value"));
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            stored.LastName = "Петров";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var confirmation = ReadFormFields(form);
        confirmation["Input.Action"] = "register";
        confirmation["Input.Confirmation"] = "true";
        confirmation["Input.AcceptRules"] = "true";
        using var registered = await client.PostAsync(path, new FormUrlEncodedContent(confirmation), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        using var check = factory.Services.CreateScope();
        var team = await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Include(item => item.Members)
            .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal(expectedName, team.MemberNames!.First());
        Assert.Equal(member.Id, Assert.Single(team.Members).Id);
    }

    [Theory]
    [InlineData("Input.TeamPrefix", " NSTU")]
    [InlineData("Input.TeamPrefix", "NSTU ")]
    [InlineData("Input.TeamSuffix", " Test Team")]
    [InlineData("Input.TeamSuffix", "Test Team ")]
    public async Task TeamNameRejectsOuterSpaces(string field, string value)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var client = await factory.LoginAsync(coach.UserName!);
        var fields = TeamFields();
        fields[field] = value;
        using var response = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains("Use Latin letters, with words separated by a single space or hyphen.",
            document.QuerySelectorAll($"[data-valmsg-for='{field}']").Select(element => element.TextContent));
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "preview")]
    [InlineData(true, "preview")]
    [InlineData(false, "register")]
    [InlineData(true, "register")]
    public async Task ManualAndProfileNamesMayCoincide(bool profileFirst, string action)
    {
        var (contest, organization, coach) = await SetupAsync();
        var member = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            stored.OrganizationId = organization.Id;
            stored.FirstName = "Иван";
            stored.LastName = "Иванов";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var fields = TeamFields();
        int profileSlot = profileFirst ? 1 : 2;
        int manualSlot = profileFirst ? 2 : 1;
        fields[$"Input.Member{profileSlot}Id"] = member.Id.ToString();
        fields[$"Input.Member{profileSlot}Name"] = "";
        fields[$"Input.Member{manualSlot}Name"] = "Иванов Иван";
        fields["Input.Action"] = action;
        using var client = await factory.LoginAsync(coach.UserName!);
        using var response = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", fields);
        Assert.Equal(action == "preview" ? HttpStatusCode.OK : HttpStatusCode.Redirect, response.StatusCode);
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OlympContext>();
        if (action == "preview")
        {
            using var document = await new HtmlParser().ParseDocumentAsync(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.NotNull(document.QuerySelector("#team-preview"));
            Assert.Equal(2, document.QuerySelectorAll("section li").Count(element => element.TextContent.Contains("Иванов Иван", StringComparison.Ordinal)));
            Assert.False(await db.Competitors.AnyAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
        }
        else
        {
            var team = await db.Competitors.Include(item => item.Members)
                .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
            Assert.Equal<string>(["Иванов Иван", "Иванов Иван", "Third Member"], team.MemberNames);
            Assert.Equal(member.Id, Assert.Single(team.Members).Id);
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task RepeatedManualTeamPostDoesNotCreateDuplicate(string culture)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        var fields = TeamFields();
        fields["Input.Member2Name"] = fields["Input.Member1Name"];
        string path = CreatePath(contest.Id, organization.Id);
        using var first = await WebAppFactory.PostFormAsync(client, path, "create-team", fields);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using var repeated = await client.PostAsync(path, new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await repeated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Contains(culture == "ru" ? "Команда с таким названием уже зарегистрирована на это соревнование."
            : "A team with this name is already registered for this contest.",
            document.QuerySelectorAll("[role=alert]:not([data-valmsg-summary])").Select(element => element.TextContent));
        using var check = factory.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .SingleAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal<string>(["First Member", "First Member", "Third Member"], saved.MemberNames);
    }

    [Theory]
    [InlineData("2", "Test Team")]
    [InlineData("1", "Other Team")]
    public async Task TeamNameUniquenessUsesNumberAndSuffix(string number, string suffix)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var client = await factory.LoginAsync(coach.UserName!);
        string path = CreatePath(contest.Id, organization.Id);
        using var first = await WebAppFactory.PostFormAsync(client, path, "create-team", TeamFields());
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var fields = TeamFields();
        fields["Input.TeamNumber"] = number;
        fields["Input.TeamSuffix"] = suffix;
        using var second = await WebAppFactory.PostFormAsync(client, path, "create-team", fields);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.Equal(2, await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .CountAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ServiceEnforcesTeamNameUniquenessPerContest(bool anotherOrganization, bool anotherContest)
    {
        var (contest, organization, coach) = await SetupAsync();
        int nextContestId = contest.Id;
        int nextOrganizationId = organization.Id;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            if (anotherOrganization)
            {
                var other = new Organization { Name = "Other university", Users = [] };
                context.Organizations.Add(other);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                nextOrganizationId = other.Id;
            }
            if (anotherContest)
            {
                var other = TestEntities.CreateContest("Other contest", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(6));
                context.Contests.Add(other);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                nextContestId = other.Id;
            }
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
                .RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "NSTU 1: Team", ["One", "Two", "Three"], TestContext.Current.CancellationToken);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            Task RegisterAsync() => service.RegisterTeamAsync(nextContestId, nextOrganizationId, coach.Id,
                "NSTU 1: Team", ["Four", "Five", "Six"], TestContext.Current.CancellationToken);
            if (anotherContest)
                await RegisterAsync();
            else
                await Assert.ThrowsAsync<OperationValidationException>(RegisterAsync);
        }
        using var check = factory.Services.CreateScope();
        Assert.Equal(anotherContest ? 2 : 1, await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .CountAsync(team => team.ContestId == contest.Id || team.ContestId == nextContestId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceAllowsCoincidingNames(bool linkedAccounts)
    {
        var (contest, organization, coach) = await SetupAsync();
        var first = await factory.AddUserAsync();
        var second = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var members = await context.OlympUsers.Where(user => user.Id == first.Id || user.Id == second.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (var member in members)
        {
            member.OrganizationId = organization.Id;
            member.LastName = "Иванов";
            member.FirstName = "Иван";
        }
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        TeamMember[] participants = linkedAccounts
            ? [new(first.Id, null), new(second.Id, null), new(null, "Иванов Иван")]
            : [new(null, "Иванов Иван"), new(null, "Иванов Иван"), new(null, "Иванов Иван")];
        await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
            .RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "Team", participants, false, TestContext.Current.CancellationToken);
        var team = await context.Competitors.Include(item => item.Members)
            .SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Repeat("Иванов Иван", 3), team.MemberNames);
        Assert.Equal(linkedAccounts ? 2 : 0, team.Members.Count);
    }

    [Fact]
    public async Task ServiceRejectsLinkedAccountWithoutAnyUsableName()
    {
        var (contest, organization, coach) = await SetupAsync();
        var member = await factory.AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
        stored.OrganizationId = organization.Id;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        TeamMember[] participants = [new(member.Id, null), new(null, "Second"), new(null, "Third")];
        await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
            .RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "Team", participants, false, TestContext.Current.CancellationToken));
        Assert.False(await context.Competitors.AnyAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PreviewLocksFieldsAndEditingRequiresNewConfirmation(string culture)
    {
        var (contest, organization, coach) = await SetupAsync();
        var member = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            stored.OrganizationId = organization.Id;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        string path = CreatePath(contest.Id, organization.Id);
        var fields = TeamFields(size: 2, outOfCompetition: true);
        fields["Input.Member1Id"] = member.Id.ToString();
        fields["Input.Action"] = "preview";
        using var preview = await WebAppFactory.PostFormAsync(client, path, "create-team", fields);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var form = document.QuerySelector("form:has(input[name='Input.TeamPrefix'])")!;
        Assert.Equal(6, form.QuerySelectorAll("input[readonly]").Length);
        Assert.Equal(3, form.QuerySelectorAll("select[disabled]").Length);
        Assert.NotNull(form.QuerySelector("input[type='checkbox'][name='Input.OutOfCompetition'][disabled][checked]"));
        Assert.Null(form.QuerySelector("input[name='Input.Confirmation'][checked], input[name='Input.AcceptRules'][checked]"));
        var editButton = form.QuerySelector("button[value='edit']")!;
        Assert.Equal(culture == "ru" ? "Изменить данные" : "Edit details", editButton.TextContent);
        var editFields = ReadFormFields(form);
        Assert.Equal(member.Id.ToString(), editFields["Input.Member1Id"]);
        Assert.Equal("true", editFields["Input.OutOfCompetition"]);
        editFields["Input.Action"] = "edit";
        editFields["Input.Confirmation"] = "true";
        editFields["Input.AcceptRules"] = "true";
        using var edited = await client.PostAsync(path, new FormUrlEncodedContent(editFields), TestContext.Current.CancellationToken);
        using var editPage = await new HtmlParser().ParseDocumentAsync(
            await edited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var editableForm = editPage.QuerySelector("form:has(input[name='Input.TeamPrefix'])")!;
        Assert.Empty(editableForm.QuerySelectorAll("input[readonly], select[disabled]"));
        Assert.NotNull(editableForm.QuerySelector("input[name='Input.OutOfCompetition']:not([disabled])[checked]"));
        Assert.Null(editableForm.QuerySelector("#team-preview, input[name='Input.Confirmation'], input[name='Input.AcceptRules']"));
        Assert.Equal(member.Id.ToString(), editableForm.QuerySelector("select[name='Input.Member1Id'] option[selected]")!.GetAttribute("value"));
        var changedFields = ReadFormFields(editableForm);
        changedFields["Input.TeamSuffix"] = "Changed Team";
        changedFields["Input.Action"] = "preview";
        using var changed = await client.PostAsync(path, new FormUrlEncodedContent(changedFields), TestContext.Current.CancellationToken);
        using var changedPage = await new HtmlParser().ParseDocumentAsync(
            await changed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal("NSTU 1: Changed Team", changedPage.QuerySelector("section p.fw-semibold > span:not(.badge)")!.TextContent);
        Assert.Null(changedPage.QuerySelector("input[name='Input.Confirmation'][checked], input[name='Input.AcceptRules'][checked]"));
        var registrationFields = ReadFormFields(changedPage.QuerySelector("form:has(input[name='Input.TeamPrefix'])")!);
        registrationFields["Input.Action"] = "register";
        using var unconfirmed = await client.PostAsync(path, new FormUrlEncodedContent(registrationFields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, unconfirmed.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .AnyAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
        registrationFields["Input.Confirmation"] = "true";
        registrationFields["Input.AcceptRules"] = "true";
        using var registered = await client.PostAsync(path, new FormUrlEncodedContent(registrationFields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        using var check = factory.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Include(team => team.Members)
            .SingleAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken);
        Assert.Equal("NSTU 1: Changed Team", saved.Name);
        Assert.True(saved.IsOutOfCompetition);
        Assert.Equal(member.Id, Assert.Single(saved.Members).Id);
        Assert.Equal(2, saved.MemberNames!.Count());
    }

    [Theory]
    [InlineData("too-few")]
    [InlineData("empty")]
    [InlineData("duplicate-account")]
    [InlineData("wrong-org-member")]
    [InlineData("rules")]
    [InlineData("confirmation")]
    [InlineData("prefix")]
    [InlineData("empty-prefix")]
    [InlineData("already-registered")]
    public async Task InvalidTeamCannotBeCreated(string invalid)
    {
        var (contest, organization, coach) = await SetupAsync();
        var fields = TeamFields();
        var member = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.OlympUsers.SingleAsync(user => user.Id == member.Id, TestContext.Current.CancellationToken);
            if (invalid != "wrong-org-member") stored.OrganizationId = organization.Id;
            stored.FirstName = "Иван";
            stored.LastName = "Иванов";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        if (invalid == "too-few") fields["Input.Member3Name"] = "";
        if (invalid == "empty")
        {
            fields["Input.Member1Name"] = fields["Input.Member2Name"] = fields["Input.Member3Name"] = "";
            fields["Input.OutOfCompetition"] = "true";
        }
        if (invalid == "duplicate-account")
        {
            fields["Input.Member1Id"] = fields["Input.Member2Id"] = member.Id.ToString();
        }
        if (invalid is "wrong-org-member" or "already-registered")
        {
            fields["Input.Member1Id"] = member.Id.ToString();
            fields["Input.Member1Name"] = "";
        }
        if (invalid == "already-registered")
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
                .RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "Existing", [member.Id.ToString(), "Other One", "Other Two"], TestContext.Current.CancellationToken);
        }
        if (invalid == "rules") fields["Input.AcceptRules"] = "false";
        if (invalid == "confirmation") fields["Input.Confirmation"] = "false";
        if (invalid == "prefix") fields["Input.TeamPrefix"] = "НГТУ";
        if (invalid == "empty-prefix") fields["Input.TeamPrefix"] = "   ";
        using var client = await factory.LoginAsync(coach.UserName!);
        using var response = await WebAppFactory.PostFormAsync(client, CreatePath(contest.Id, organization.Id), "create-team", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await new HtmlParser().ParseDocumentAsync(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.NotEmpty(document.QuerySelectorAll(".validation-message, .validation-summary-errors li, [role=alert]:not([data-valmsg-summary])"));
        string? errorField = invalid switch
        {
            "duplicate-account" => "Input.Member2Id",
            "already-registered" => "Input.Member1Id",
            _ => null
        };
        if (errorField is not null)
        {
            string expectedError = invalid == "already-registered"
                ? "One or more members are already registered for this contest."
                : "Team members must be different.";
            Assert.Contains(expectedError, document.QuerySelectorAll($"[data-valmsg-for='{errorField}']")
                .Select(element => element.TextContent));
        }
        using var check = factory.Services.CreateScope();
        Assert.Equal(invalid == "already-registered" ? 1 : 0,
            await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors.CountAsync(team => team.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public async Task CancellationHonorsApprovalOutOfCompetitionAndStart(bool approved, bool outOfCompetition, bool started, bool allowed)
    {
        var (contest, organization, coach) = await SetupAsync();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
                .RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "Team", [new TeamMember(null, "First"), new(null, "Second"), new(null, "Third")], outOfCompetition, TestContext.Current.CancellationToken);
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await context.Competitors.SingleAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken);
            team.IsApproved = approved;
            if (started)
                (await context.Contests.SingleAsync(item => item.Id == contest.Id, TestContext.Current.CancellationToken)).StartTime = DateTimeOffset.UtcNow.AddHours(-1);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        int teamId;
        using (var scope = factory.Services.CreateScope())
            teamId = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors.Where(team => team.ContestId == contest.Id)
                .Select(team => team.Id).SingleAsync(TestContext.Current.CancellationToken);
        using var response = await CancelAsync(client, contest.Id, teamId);
        Assert.Equal(allowed ? HttpStatusCode.Redirect : HttpStatusCode.OK, response.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.Equal(!allowed, await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors
            .AnyAsync(team => team.Id == teamId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task CoachCanReadUniversityTeamsButCannotCancelOthersOrSeeTheirPasswords(string culture)
    {
        var (contest, organization, coach) = await SetupAsync();
        var other = await factory.AddUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CompetitorManagementService>();
            await service.RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "Own Team", ["One", "Two", "Three"], TestContext.Current.CancellationToken);
            await service.RegisterTeamAsync(contest.Id, organization.Id, other.Id, "Other Team", ["Four", "Five", "Six"], TestContext.Current.CancellationToken);
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var teams = await context.Competitors.Where(team => team.ContestId == contest.Id).ToListAsync(TestContext.Current.CancellationToken);
            foreach (var team in teams)
            {
                team.PasswordHash = team.CoachId == coach.Id ? "own-visible-password" : "other-private-password";
                team.RegistrationDate = team.CoachId == coach.Id ? new DateTimeOffset(2026, 1, 17, 12, 30, 0, TimeSpan.Zero) : null;
            }
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);
        using var response = await client.GetAsync(ListPath(contest.Id), TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = await new HtmlParser().ParseDocumentAsync(html, TestContext.Current.CancellationToken);
        Assert.Equal(culture == "ru" ? "Дата регистрации" : "Registration date", document.QuerySelector("main th:nth-last-child(2)")!.TextContent);
        var rows = document.QuerySelectorAll("main tbody tr");
        var ownRow = Assert.Single(rows, row => row.QuerySelector(".fw-semibold")!.TextContent == "Own Team");
        Assert.Equal(new DateTimeOffset(2026, 1, 17, 12, 30, 0, TimeSpan.Zero).ToLocalTime().ToString("d", CultureInfo.GetCultureInfo(culture)),
            ownRow.QuerySelector("td:nth-last-child(2)")!.TextContent);
        var otherRow = Assert.Single(rows, row => row.QuerySelector(".fw-semibold")!.TextContent == "Other Team");
        Assert.Empty(otherRow.QuerySelector("td:nth-last-child(2)")!.TextContent);
        Assert.Contains("Other Team", html);
        Assert.Contains("own-visible-password", html);
        Assert.DoesNotContain("other-private-password", html);
        int otherTeamId;
        using (var scope = factory.Services.CreateScope())
            otherTeamId = await scope.ServiceProvider.GetRequiredService<OlympContext>().Competitors
                .Where(team => team.ContestId == contest.Id && team.CoachId == other.Id).Select(team => team.Id).SingleAsync(TestContext.Current.CancellationToken);
        using var forbidden = await CancelAsync(client, contest.Id, otherTeamId);
        Assert.Equal(HttpStatusCode.OK, forbidden.StatusCode);
        using var check = factory.Services.CreateScope();
        Assert.True(await check.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(team => team.Id == otherTeamId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TeamRegistrationRequiresCoachAndAuthorizedUniversity()
    {
        var (contest, organization, coach) = await SetupAsync();
        using var guest = factory.CreateSiteClient();
        using var challenged = await guest.GetAsync(ListPath(contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        var ordinary = await factory.AddUserAsync();
        using var ordinaryClient = await factory.LoginAsync(ordinary.UserName!);
        using var denied = await ordinaryClient.GetAsync(ListPath(contest.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
        using var client = await factory.LoginAsync(coach.UserName!);
        using var wrongOrg = await client.GetAsync(CreatePath(contest.Id, organization.Id + 10000), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrg.StatusCode);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("individual")]
    [InlineData("ended")]
    public async Task UnavailableContestCannotCreateTeams(string reason)
    {
        var (contest, organization, coach) = await SetupAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var stored = await context.Contests.SingleAsync(item => item.Id == contest.Id, TestContext.Current.CancellationToken);
            if (reason == "hidden") stored.IsHidden = true;
            if (reason == "individual") stored.Abbr = "nstu-test";
            if (reason == "ended")
            {
                stored.StartTime = DateTimeOffset.UtcNow.AddHours(-6);
                stored.EndTime = DateTimeOffset.UtcNow.AddHours(-1);
            }
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = await factory.LoginAsync(coach.UserName!);
        foreach (string path in new[] { ListPath(contest.Id), CreatePath(contest.Id, organization.Id) })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("register")]
    public async Task FinishedContestRejectsPreviouslyOpenedTeamForm(string action)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var client = await factory.LoginAsync(coach.UserName!);
        string path = CreatePath(contest.Id, organization.Id);
        using var page = await client.GetAsync(path, TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var fields = ReadFormFields(document.QuerySelector("main form")!);
        foreach (var field in TeamFields())
            fields[field.Key] = field.Value;
        fields["Input.Action"] = action;
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var stored = await context.Contests.SingleAsync(item => item.Id == contest.Id, TestContext.Current.CancellationToken);
        stored.StartTime = DateTimeOffset.UtcNow.AddHours(-6);
        stored.EndTime = DateTimeOffset.UtcNow.AddHours(-1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var content = new FormUrlEncodedContent(fields);
        using var posted = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
        Assert.False(posted.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.False(await context.Competitors.AnyAsync(item => item.ContestId == contest.Id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task ServiceRejectsInvalidTeamSizes(int size, bool outOfCompetition)
    {
        var (contest, organization, coach) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        await Assert.ThrowsAsync<OperationValidationException>(() => scope.ServiceProvider.GetRequiredService<CompetitorManagementService>()
            .RegisterTeamAsync(contest.Id, organization.Id, coach.Id, "Team", [.. Enumerable.Range(1, size).Select(i => new TeamMember(null, $"Member {i}"))], outOfCompetition, TestContext.Current.CancellationToken));
    }

    private async Task<(Contest Contest, Organization Organization, OlympUser Coach)> SetupAsync()
    {
        var coach = await factory.AddUserAsync();
        var contest = TestEntities.CreateContest("Team contest " + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(6));
        var organization = new Organization { Name = "Test university " + Guid.NewGuid().ToString("N"), ACMName = "NSTU", Users = [] };
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OlympContext>();
        context.Contests.Add(contest);
        context.Organizations.Add(organization);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var storedCoach = await users.FindByIdAsync(coach.Id.ToString());
        Assert.True((await users.AddClaimAsync(storedCoach!, new Claim(ClaimNames.CoachOrg, organization.Id.ToString()))).Succeeded);
        return (contest, organization, coach);
    }

    private static Dictionary<string, string> TeamFields(int size = 3, bool outOfCompetition = false)
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.TeamPrefix"] = "NSTU",
            ["Input.TeamNumber"] = "1",
            ["Input.TeamSuffix"] = "Test Team",
            ["Input.Action"] = "register",
            ["Input.OutOfCompetition"] = outOfCompetition.ToString(),
            ["Input.Confirmation"] = "true",
            ["Input.AcceptRules"] = "true"
        };
        for (int i = 1; i <= size; i++) fields[$"Input.Member{i}Name"] = new[] { "First Member", "Second Member", "Third Member" }[i - 1];
        return fields;
    }

    private static Dictionary<string, string> ReadFormFields(IElement form)
    {
        var fields = new Dictionary<string, string>();
        foreach (var input in form.QuerySelectorAll("input[name]:not([disabled])"))
        {
            if (input.GetAttribute("type") == "checkbox" && !input.HasAttribute("checked"))
                continue;
            fields[input.GetAttribute("name")!] = input.GetAttribute("value") ?? "";
        }
        foreach (var select in form.QuerySelectorAll("select[name]:not([disabled])"))
        {
            var option = select.QuerySelector("option[selected]") ?? select.QuerySelector("option");
            fields[select.GetAttribute("name")!] = option?.GetAttribute("value") ?? "";
        }
        return fields;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviouslyOpenedCancellationFormRechecksCoachAndApproval(bool changeCoach)
    {
        var token = TestContext.Current.CancellationToken;
        var (contest, organization, coach) = await SetupAsync();
        using (var setup = factory.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<CompetitorManagementService>().RegisterTeamAsync(
                contest.Id, organization.Id, coach.Id, "Own team", ["One", "Two", "Three"], token);
        using var client = await factory.LoginAsync(coach.UserName!);
        using var page = await client.GetAsync(ListPath(contest.Id), token);
        using var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelector("form:has(button[name='CancelTeamId'])")!;
        Assert.NotNull(form);
        var fields = ReadFormFields(form);
        string teamId = form.QuerySelector("button[name='CancelTeamId']")!.GetAttribute("value")!;
        fields["CancelTeamId"] = teamId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var team = await db.Competitors.SingleAsync(item => item.Id == int.Parse(teamId), token);
            if (changeCoach)
                team.CoachId = null;
            else
                team.IsApproved = true;
            await db.SaveChangesAsync(token);
        }

        using var response = await client.PostAsync(ListPath(contest.Id), new FormUrlEncodedContent(fields), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var verification = factory.Services.CreateScope();
        Assert.True(await verification.ServiceProvider.GetRequiredService<OlympContext>().Competitors.AnyAsync(item => item.Id == int.Parse(teamId), token));
    }

    private static string ListPath(int id) => $"/contests/{id}/registration/teams";
    private static string CreatePath(int id, int orgId) => $"/contests/{id}/registration/teams/create?orgId={orgId}";

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, int id, int teamId) =>
        WebAppFactory.PostFormAsync(client, ListPath(id), "cancel-team", new() { ["CancelTeamId"] = teamId.ToString() });
}
