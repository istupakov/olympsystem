using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Services.Checker;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class ContestImportPageTests
{
    [Fact]
    public async Task CheckingAllowsLimitEditsAndTaskSwitchingAndCancellationKeepsSelection()
    {
        var user = await _factory.AddUserAsync();
        await GrantRoleAsync(user.Id, RoleNames.Admin);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT 1 FROM Checkers WHERE Id = 4) BEGIN SET IDENTITY_INSERT Checkers ON; INSERT INTO Checkers (Id, Description) VALUES (4, N'Strict'); SET IDENTITY_INSERT Checkers OFF; END", Token);
        var contest = TestEntities.CreateContest("Background import", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var info = new GccLinuxCpp23Configuration().ParseInfo("15.2.0") with { FullName = Guid.NewGuid().ToString("N") };
        var compiler = new Compilator
        {
            Name = info.Configuration.Name,
            Description = info.FullName,
            Language = info.Configuration.Language,
            ConfigName = info.Configuration.ConfigName,
            SourceExtension = info.Configuration.SourceExtension,
            CommandLine = info.Configuration.CompileCommand,
            IsActive = true,
            Submissions = []
        };
        db.Contests.Add(contest);
        db.Compilators.Add(compiler);
        var alternative = TestEntities.CreateCompilator();
        alternative.Language = "C++";
        alternative.SourceExtension = ".cpp";
        db.Compilators.Add(alternative);
        await db.SaveChangesAsync(Token);
        var runner = new WaitingImportChecker(info);
        var manager = new CheckerManager(_factory.Services, scope.ServiceProvider.GetRequiredService<IDbContextFactory<OlympContext>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CheckerManager>.Instance);
        manager.Register(runner, Token);
        await RenderAsync<ContestImport>($"/jury/contests/{contest.Id}/import", new() { [nameof(ContestImport.ContestId)] = contest.Id }, Staff(user.Id), async page =>
        {
            await ActAsync(page, "UploadAsync", new InputFileChangeEventArgs([new MemoryFile(ProblemArchives.Zip(
                ("A/problem.json", "{\"number\":1,\"name\":\"First\"}"u8.ToArray()),
                ("A/tests/input1.txt", "1"u8.ToArray()), ("A/tests/output1.txt", "1"u8.ToArray()),
                ("A/solutions/main.cpp", "source"u8.ToArray()),
                ("B/problem.json", "{\"number\":2,\"name\":\"Second\"}"u8.ToArray()),
                ("B/tests/input1.txt", "1"u8.ToArray()), ("B/tests/output1.txt", "1"u8.ToArray())))]));
            var preview = Preview(page);
            var first = preview.Rows[0];
            first.Solutions[0].CompilerId = compiler.Id;
            var checking = ActAsync(page, "RunSolutionsAsync");
            try
            {
                await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.True(first.Solutions[0].Save);
                Assert.True(first.IsChecking);
                Assert.False(Assert.IsType<bool>(Field(page, "_busy")));
                first.TimeLimit = 7;
                await ActAsync(page, "LimitsChanged");
                await SelectAsync(page, preview.Rows[1].Folder);
                Assert.Same(preview.Rows[1], Field(page, "_selected"));
                Assert.False(checking.IsCompleted);
                Assert.False(first.ReferencesChecked);
            }
            finally { await ActAsync(page, "StopChecking", first); }
            await checking;
            Assert.False(first.IsChecking);
            Assert.Equal(7, first.TimeLimit);
            Assert.Same(preview.Rows[1], Field(page, "_selected"));
            Assert.Null(first.CheckError);
        }, services => services.AddSingleton<ICheckerManager>(manager));
    }

    private sealed class WaitingImportChecker(CompilatorInfo info) : ICheckerService
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name => "Waiting import runner";
        public DateTimeOffset ConnectingTime => DateTimeOffset.UtcNow;
        public IReadOnlyList<ICompilatorConfiguration> Configurations => [info.Configuration];
        public IReadOnlyList<CompilatorInfo>? Compilators => [info];
        public CheckerSelfTestingStatus SelfTestingStatus => CheckerSelfTestingStatus.Success;
        public IEnumerable<CheckerTestResult> SelfTestingResults => [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> RunSelfTests(CancellationToken token) => Task.FromResult(true);
        public Task<CheckerResult> Run(string source, ICompilatorConfiguration configuration, string input, TimeSpan timeLimit, int memoryLimitMb, CancellationToken token) => throw new NotSupportedException();
        public async Task<CheckerResult> Check(CheckRequest request, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The test must cancel this check.");
        }
        public Task<ValidatorResult> Validate(string source, CompilatorInfo compiler, IReadOnlyList<ValidatorTest> tests, CancellationToken token) =>
            Task.FromResult(new ValidatorResult(true, "Rejected stored input", [.. tests.Select(test => new ValidatorTestResult(test.Number, false))]));
    }
}
