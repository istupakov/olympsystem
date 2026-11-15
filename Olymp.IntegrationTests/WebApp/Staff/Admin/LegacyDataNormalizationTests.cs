using System.Net;
using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Admin;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Administration;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public class LegacyDataNormalizationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Theory]
    [InlineData(1, "Шатров Д. ФПМИ, ПММ-51", "Шатров Д., ФПМИ, ПММ-51")]
    [InlineData(1, "Абалов Н.  АВТФ, АМ-610", "Абалов Н., АВТФ, АМ-610")]
    [InlineData(1, "Кротенко В. ФПМИ, ПМ-83 ", "Кротенко В., ФПМИ, ПМ-83")]
    [InlineData(3, "Гатилов С. Ю. (НГУ)", "Гатилов С. Ю., НГУ")]
    [InlineData(3, "Черкасов А.Н. (СГУПС)", "Черкасов А.Н., СГУПС")]
    [InlineData(14, "Юдкин Е. ПМ-02", "Юдкин Е., ПМ-02")]
    [InlineData(14, "Трифонов К. ПМИ-01", "Трифонов К., ПМИ-01")]
    [InlineData(3, "Рубан А. А.", "Рубан А. А.")]
    [InlineData(3, "Иванов А. (Неизвестный вуз)", "Иванов А. (Неизвестный вуз)")]
    [InlineData(1, "Фамилия ФПМИ, ПМ-01", "Фамилия ФПМИ, ПМ-01")]
    [InlineData(14, "Юдкин Е. ПМ-99", "Юдкин Е. ПМ-99")]
    [InlineData(2, "Шатров Д. ФПМИ, ПММ-51", "Шатров Д. ФПМИ, ПММ-51")]
    public void OnlyKnownFormatsAreNormalizedAndRepeatedNormalizationIsStable(int contestId, string before, string after)
    {
        Assert.Equal(after, LegacyDataNormalizationService.NormalizeParticipantName(contestId, before));
        Assert.Equal(after, LegacyDataNormalizationService.NormalizeParticipantName(contestId, after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedPreviewAndApplyPreserveHistoryAndRemoveOnlyAgreedDuplicatesAndUnusedInactiveCompilers(bool normalized)
    {
        await using var isolatedFactory = new WebAppFactory();
        await isolatedFactory.InitializeAsync();

        async Task<LegacyDataNormalizationService.Preview> PrepareAsync(ClaimsPrincipal user)
        {
            await using var scope = isolatedFactory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<LegacyDataNormalizationService>().PrepareAsync(user, TestContext.Current.CancellationToken);
        }

        async Task<int> ApplyAsync(ClaimsPrincipal user, LegacyDataNormalizationService.Preview plan)
        {
            await using var scope = isolatedFactory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<LegacyDataNormalizationService>().ApplyAsync(user, plan, TestContext.Current.CancellationToken);
        }

        var admin = await isolatedFactory.AddUserAsync();
        var actor = Actor(admin.Id, RoleNames.Admin);
        int participantId, problemId;
        Dictionary<int, int> compilerBySubmission;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            foreach (var rule in LegacyCompilerNames.Rules)
            {
                db.Compilators.Add(new Compilator
                {
                    Id = rule.Id,
                    Name = normalized ? rule.Name : rule.OldName,
                    Description = normalized ? rule.Description : null,
                    Language = rule.Language,
                    SourceExtension = rule.Extension,
                    ConfigName = rule.Configuration,
                    CommandLine = rule.Command,
                    Submissions = []
                });
            }
            foreach (var pair in LegacyCompilerNames.Merges)
            {
                var source = LegacyCompilerNames.Rules.Single(item => item.Id == pair.SourceId);
                db.Compilators.Add(new Compilator
                {
                    Id = pair.DestinationId,
                    Name = pair.Name,
                    Description = pair.Description,
                    Language = source.Language,
                    SourceExtension = source.Extension,
                    ConfigName = source.Configuration,
                    CommandLine = source.Command,
                    IsActive = pair.DestinationId != 102,
                    Submissions = []
                });
            }
            var activeEmpty = TestEntities.CreateCompilator();
            activeEmpty.Id = 101;
            var inactiveEmpty = TestEntities.CreateCompilator();
            inactiveEmpty.Id = 112;
            inactiveEmpty.IsActive = false;
            db.AddRange(activeEmpty, inactiveEmpty);
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Compilators] ON", TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Compilators] OFF", TestContext.Current.CancellationToken);

            var contest = TestEntities.CreateContest("НГТУ 2010", new DateTimeOffset(2010, 3, 19, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2010, 3, 19, 15, 0, 0, TimeSpan.Zero));
            contest.Id = 1;
            contest.Abbr = "nstu-2010";
            db.Contests.Add(contest);
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Contests] ON", TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Contests] OFF", TestContext.Current.CancellationToken);

            var participant = TestEntities.CreateCompetitor(contest, "Шатров Д. ФПМИ, ПММ-51");
            participant.CoachId = admin.Id;
            participant.Members.Add((await db.OlympUsers.FindAsync([admin.Id], TestContext.Current.CancellationToken))!);
            participant.MemberInfos = $"Roster name;{admin.Id}";
            var problem = TestEntities.CreateProblem(contest);
            db.AddRange(participant, problem);
            foreach (var compiler in db.Compilators.Local.Where(item => item.Id is not (60 or 98 or 101 or 102 or 112)).ToArray())
            {
                var submission = TestEntities.CreateSubmission(participant, problem, compiler, contest.StartTime.AddMinutes(10), [1, 2, 3]);
                submission.StatusCode = compiler.Id == 96 ? 0 : 2;
                submission.CheckResults.Add(new CheckResult
                {
                    Submission = submission,
                    CheckTime = contest.EndTime,
                    StatusCode = 2,
                    Log = $"History {compiler.Id}",
                    TestResults = "[]"
                });
                db.Submissions.Add(submission);
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            participantId = participant.Id;
            problemId = problem.Id;
            compilerBySubmission = await db.Submissions.ToDictionaryAsync(item => item.Id, item => item.CompilatorId, TestContext.Current.CancellationToken);
        }

        var preview = await PrepareAsync(actor);
        Assert.Equal(normalized ? 0 : 63, preview.Compilers.Length);
        Assert.Equal(5, preview.Merges.Length);
        Assert.Equal([60, 98, 112], preview.EmptyCompilers.Select(item => item.Id));
        Assert.Equal(0, preview.UnrecognizedCompilers);
        Assert.Equal(0, preview.UnrecognizedPairs);
        Assert.False(preview.StandingOrderChanges);
        Assert.Equal("Шатров Д., ФПМИ, ПММ-51", Assert.Single(preview.Participants).After);
        Assert.All(preview.Merges, item => Assert.Single(item.SubmissionIds));
        Assert.All(preview.Compilers, item => Assert.DoesNotContain("MinGW", item.DescriptionAfter!));

        await using (var verify = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<OlympContext>();
            Assert.Equal(preview.Participants[0].Before, (await db.Competitors.FindAsync([participantId], TestContext.Current.CancellationToken))!.Name);
            Assert.NotNull(await db.Compilators.FindAsync([112], TestContext.Current.CancellationToken));
            var changed = (await db.Compilators.FindAsync([104], TestContext.Current.CancellationToken))!;
            changed.CommandLine += " changed";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<OperationValidationException>(() => ApplyAsync(actor, preview));
        Assert.Equal(1, (await PrepareAsync(actor)).UnrecognizedPairs);
        await using (var verify = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<OlympContext>();
            var changed = (await db.Compilators.FindAsync([104], TestContext.Current.CancellationToken))!;
            changed.CommandLine = preview.Merges.Single(item => item.Destination.Id == 104).Destination.Command;
            var empty = (await db.Compilators.FindAsync([112], TestContext.Current.CancellationToken))!;
            db.Submissions.Add(TestEntities.CreateSubmission(
                (await db.Competitors.FindAsync([participantId], TestContext.Current.CancellationToken))!,
                (await db.Problems.FindAsync([problemId], TestContext.Current.CancellationToken))!, empty, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<OperationValidationException>(() => ApplyAsync(actor, preview));
        await using (var verify = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Submissions.Where(item => item.CompilatorId == 112).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            var original = await db.Submissions.SingleAsync(item => item.CompilatorId == 96, TestContext.Current.CancellationToken);
            var newSubmission = TestEntities.CreateSubmission(
                (await db.Competitors.FindAsync([participantId], TestContext.Current.CancellationToken))!,
                (await db.Problems.FindAsync([problemId], TestContext.Current.CancellationToken))!,
                (await db.Compilators.FindAsync([96], TestContext.Current.CancellationToken))!, DateTimeOffset.UtcNow);
            db.Submissions.Add(newSubmission);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.Submissions.Where(item => item.Id == original.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.CompilatorId, 104), TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<OperationValidationException>(() => ApplyAsync(actor, preview));
        await using (var verify = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Submissions.Where(item => item.CompilatorId == 96).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            var originalId = compilerBySubmission.Single(item => item.Value == 96).Key;
            await db.Submissions.Where(item => item.Id == originalId).ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.CompilatorId, 96), TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectCompilerDelete ON Compilators INSTEAD OF DELETE AS THROW 51000, 'Test rollback', 1;", TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(actor, preview));
        await using (var verify = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectCompilerDelete", TestContext.Current.CancellationToken);
            Assert.Equal(preview.Participants[0].Before, (await db.Competitors.FindAsync([participantId], TestContext.Current.CancellationToken))!.Name);
            Assert.Equal(normalized ? "GCC C++11" : "GNU C++ 4.8.1 (MinGW)", (await db.Compilators.FindAsync([20], TestContext.Current.CancellationToken))!.Name);
            foreach (var (submissionId, compilerId) in compilerBySubmission)
                Assert.Equal(compilerId, (await db.Submissions.FindAsync([submissionId], TestContext.Current.CancellationToken))!.CompilatorId);
        }
        Assert.Equal(1 + (normalized ? 0 : 63) + 5 + 3, await ApplyAsync(actor, preview));
        var repeated = await PrepareAsync(actor);
        Assert.Empty(repeated.Participants);
        Assert.Empty(repeated.Compilers);
        Assert.Empty(repeated.Merges);
        Assert.Empty(repeated.EmptyCompilers);
        Assert.Equal(0, await ApplyAsync(actor, repeated));
        await Assert.ThrowsAsync<OperationValidationException>(() => ApplyAsync(actor, preview));

        await using var finalScope = isolatedFactory.Services.CreateAsyncScope();
        var context = finalScope.ServiceProvider.GetRequiredService<OlympContext>();
        var saved = await context.Competitors.Include(item => item.Members).SingleAsync(item => item.Id == participantId, TestContext.Current.CancellationToken);
        Assert.Equal("Шатров Д., ФПМИ, ПММ-51", saved.Name);
        Assert.Equal(admin.Id, saved.CoachId);
        Assert.Equal(admin.Id, Assert.Single(saved.Members).Id);
        Assert.Equal($"Roster name;{admin.Id}", saved.MemberInfos);
        Assert.Equal(admin.Name, saved.Members.Single().Name);
        Assert.Equal(compilerBySubmission.Count, await context.Submissions.CountAsync(TestContext.Current.CancellationToken));
        foreach (var (submissionId, compilerId) in compilerBySubmission)
        {
            var submission = await context.Submissions.Include(item => item.CheckResults).SingleAsync(item => item.Id == submissionId, TestContext.Current.CancellationToken);
            var pair = LegacyCompilerNames.Merges.SingleOrDefault(item => item.SourceId == compilerId);
            Assert.Equal(pair?.DestinationId ?? compilerId, submission.CompilatorId);
            Assert.Equal(compilerId == 96 ? 0 : 2, submission.StatusCode);
            Assert.Equal([1, 2, 3], submission.SourceCode);
            Assert.Equal($"History {compilerId}", Assert.Single(submission.CheckResults).Log);
            Assert.Equal(participantId, submission.UserId);
            Assert.Equal(problemId, submission.ProblemId);
        }
        foreach (var merge in preview.Merges)
        {
            Assert.Null(await context.Compilators.FindAsync([merge.Source.Id], TestContext.Current.CancellationToken));
            Assert.Equal(merge.Destination, LegacyDataNormalizationService.CompilerSnapshot.From(
                (await context.Compilators.FindAsync([merge.Destination.Id], TestContext.Current.CancellationToken))!));
        }
        foreach (var empty in preview.EmptyCompilers)
            Assert.Null(await context.Compilators.FindAsync([empty.Id], TestContext.Current.CancellationToken));
        Assert.True((await context.Compilators.FindAsync([101], TestContext.Current.CancellationToken))!.IsActive);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(RoleNames.Jury)]
    public async Task NonAdministratorCannotPreviewOrApply(string? role)
    {
        var account = await factory.AddUserAsync();
        var actor = Actor(account.Id, role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PrepareAsync(actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApplyAsync(actor, new([], [], [], [], false, [])));
    }

    [Fact]
    public async Task ProductionDifferencesShowExpectedAndActualValuesAndAreRecheckedEvenWhenCountsStayTheSame()
    {
        var admin = await factory.AddUserAsync();
        var actor = Actor(admin.Id, RoleNames.Admin);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            foreach (var id in new[] { 20, 64, 96 })
            {
                var rule = LegacyCompilerNames.Rules.Single(item => item.Id == id);
                db.Compilators.Add(new Compilator
                {
                    Id = id,
                    Name = rule.OldName,
                    Description = null,
                    Language = rule.Language,
                    SourceExtension = rule.Extension,
                    ConfigName = rule.Configuration,
                    CommandLine = rule.Command,
                    Submissions = []
                });
            }
            var different = db.Compilators.Local.Single(item => item.Id == 20);
            different.CommandLine = "another-command";
            different.IsActive = true;
            var destination = TestEntities.CreateCompilator();
            destination.Id = 104;
            destination.Name = "C# .NET";
            destination.Description = "C# 15 (.NET 11)";
            destination.CommandLine = "dotnet-csc.cmd !.!";
            destination.ConfigName = "dotnet";
            db.Compilators.Add(destination);
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Compilators] ON", TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Compilators] OFF", TestContext.Current.CancellationToken);
        }

        var preview = await PrepareAsync(actor);
        Assert.Equal(1, preview.UnrecognizedCompilers);
        Assert.Equal(2, preview.UnrecognizedPairs);
        Assert.Empty(preview.Merges);
        Assert.Contains(preview.Skipped, item => item.Id == 20 && item.Field == SiteResources.StaffCommandLine
            && item.Expected == LegacyCompilerNames.Rules.Single(rule => rule.Id == 20).Command && item.Actual == "another-command");
        Assert.Contains(preview.Skipped, item => item.Id == 20 && item.Field == SiteResources.StaffActive
            && item.Expected == SiteResources.StaffNo && item.Actual == SiteResources.StaffYes);
        Assert.Contains(preview.Skipped, item => item.Id == 109 && item.MergeSourceId == 64 && item.MergeDestinationId == 109
            && item.Field == SiteResources.StaffNormalizationRecord && item.Actual == SiteResources.StaffNo);
        Assert.Contains(preview.Skipped, item => item.Id == 104 && item.MergeSourceId == 96 && item.MergeDestinationId == 104
            && item.Field == SiteResources.StaffDescription && item.Expected == "C# 14 (.NET 10)" && item.Actual == "C# 15 (.NET 11)");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var authentication = (IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
            authentication.SetAuthenticationState(Task.FromResult(new AuthenticationState(actor)));
            await using var renderer = new MigrationRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = renderer.BeginRenderingComponent(typeof(LegacyDataNormalization), ParameterView.Empty);
                await root.QuiescenceTask;
                typeof(LegacyDataNormalization).GetField("_preview", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(renderer.Page, preview);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer.Page, null);
                using var document = await new HtmlParser().ParseDocumentAsync(root.ToHtmlString(), TestContext.Current.CancellationToken);
                var section = Assert.Single(document.QuerySelectorAll("section"), item => item.QuerySelector("h2")?.TextContent == SiteResources.StaffNormalizationSkipped);
                Assert.Equal(preview.Skipped.Length, section.QuerySelectorAll("tbody tr").Length);
                Assert.Contains("C# 14 (.NET 10)", section.TextContent);
                Assert.Contains("C# 15 (.NET 11)", section.TextContent);
                Assert.Contains("another-command", section.TextContent);
            });
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
            var destination = (await db.Compilators.FindAsync([104], TestContext.Current.CancellationToken))!;
            destination.Description = "C# 16 (.NET 12)";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var updated = await PrepareAsync(actor);
        Assert.Equal(preview.UnrecognizedPairs, updated.UnrecognizedPairs);
        Assert.Equal(preview.Skipped.Length, updated.Skipped.Length);
        await Assert.ThrowsAsync<OperationValidationException>(() => ApplyAsync(actor, preview));
        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<OlympContext>();
        Assert.Equal("C# 16 (.NET 12)", (await context.Compilators.FindAsync([104], TestContext.Current.CancellationToken))!.Description);
        Assert.NotNull(await context.Compilators.FindAsync([96], TestContext.Current.CancellationToken));
    }

    private sealed class MigrationRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : StaticHtmlRenderer(services, loggerFactory)
    {
        public LegacyDataNormalization? Page { get; private set; }

        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode)
        {
            var component = componentActivator.CreateInstance(componentType);
            if (component is LegacyDataNormalization page) Page = page;
            return component;
        }
    }

    [Fact]
    public async Task GuestCannotOpenMigrationPage()
    {
        using var client = factory.CreateSiteClient();
        using var response = await client.GetAsync("/admin/normalization", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("login", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LegacyDataNormalizationService.Preview> PrepareAsync(ClaimsPrincipal actor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LegacyDataNormalizationService>().PrepareAsync(actor, TestContext.Current.CancellationToken);
    }

    private async Task<int> ApplyAsync(ClaimsPrincipal actor, LegacyDataNormalizationService.Preview preview)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LegacyDataNormalizationService>().ApplyAsync(actor, preview, TestContext.Current.CancellationToken);
    }

    private static ClaimsPrincipal Actor(int id, string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        return new(new ClaimsIdentity(claims, "Test"));
    }
}
