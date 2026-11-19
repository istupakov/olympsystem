using System.Security.Claims;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Checker;
using Olymp.Checker.Compilators;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    private async Task<(Compilator Compiler, ImportChecker Runner)> AddReferenceCompilerAsync(OlympContext db)
    {
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
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(Token);
        var runner = new ImportChecker(info);
        ((CheckerManager)site.Services.GetRequiredService<ICheckerManager>()).Register(runner, Token);
        return (compiler, runner);
    }

    [Fact]
    public async Task ReferencesRequirePreviewResultsAndReplacingRetainsHistoryAndAvoidsDuplicates()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, _) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "References" }, true,
            ("A/solutions/author.cpp", Encoding.UTF8.GetBytes("// исходное решение"))));
        var row = preview.Rows[0];
        var file = Assert.Single(row.Solutions);
        file.Save = true;
        file.CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(SiteResources.ImportReferenceChecksRequired, error.Message);
        Assert.False(await db.Problems.AnyAsync(p => p.ContestId == setup.Contest.Id, Token));
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.True(row.IsSaved);
        var original = await db.Submissions.AsTracking().SingleAsync(s => s.ProblemId == row.Id, Token);
        Assert.True(original.IsReference);
        Assert.True(original.IsHidden);
        Assert.Equal("author.cpp", original.Description);
        Assert.Equal("// исходное решение", original.Text);
        Assert.Equal(-1001, original.StatusCode);
        Assert.Equal(int.Parse(setup.Actor.FindFirstValue(ClaimTypes.NameIdentifier)!), original.UserId);
        Assert.Single(row.Runs);
        original.StatusCode = 2;
        db.CheckResults.Add(new() { Submission = original, CheckTime = DateTimeOffset.UtcNow, StatusCode = 2, Log = "original history" });
        await db.SaveChangesAsync(Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == row.Id, Token));

        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id = row.Id }, true,
            ("A/solutions/author.cpp", Encoding.UTF8.GetBytes("// новое решение"))));
        row = preview.Rows[0];
        file = Assert.Single(row.Solutions);
        Assert.Equal([original.Id], file.ReplacedIds);
        Assert.False(file.AlreadySaved);
        file.Save = true;
        file.CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        db.ChangeTracker.Clear();
        var stored = await db.Submissions.Where(s => s.ProblemId == row.Id).OrderBy(s => s.Id).ToListAsync(Token);
        Assert.Equal(2, stored.Count);
        Assert.False(stored[0].IsReference);
        Assert.Equal(2, stored[0].StatusCode);
        Assert.Equal("// исходное решение", stored[0].Text);
        Assert.True(stored[1].IsReference);
        Assert.Equal("// новое решение", stored[1].Text);
        Assert.Equal(2, await db.CheckResults.CountAsync(r => r.SubmissionId == original.Id, Token));
        Assert.True(await db.CheckResults.AnyAsync(r => r.SubmissionId == original.Id && r.Log == "original history", Token));
        Assert.True(Assert.Single(row.Solutions).AlreadySaved);
        Assert.Single(row.ReferenceSolutions);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(2, await db.Submissions.CountAsync(s => s.ProblemId == row.Id, Token));
    }

    [Fact]
    public async Task ConcurrentReferenceReplacementRequiresReviewAndInvalidSelectionLeavesDatabaseUntouched()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, _) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Concurrent" }));
        await service.EnsureValidatorAsync(setup.Actor, preview, preview.Rows[0], Token);
        int id = await service.SaveAsync(setup.Actor, preview, preview.Rows[0], Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id }, true,
            ("A/solutions/author.cpp", "// new solution"u8.ToArray())));
        var row = preview.Rows[0];
        var file = Assert.Single(row.Solutions);
        file.Save = true;
        file.CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        var original = new Submission
        {
            User = null!,
            UserId = int.Parse(setup.Actor.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Problem = null!,
            ProblemId = id,
            Compilator = null!,
            CompilatorId = compiler.Id,
            SourceCode = "// concurrent solution"u8.ToArray(),
            Description = "author.cpp",
            IsReference = true,
            StatusCode = 2,
            CommitTime = DateTimeOffset.UtcNow,
            LastModification = DateTimeOffset.UtcNow,
            CheckResults = []
        };
        db.Submissions.Add(original);
        await db.SaveChangesAsync(Token);
        var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(SiteResources.ImportReferencesChanged, error.Message);
        Assert.Equal([original.Id], file.ReplacedIds);
        Assert.True(await db.Submissions.Where(s => s.Id == original.Id).Select(s => s.IsReference).SingleAsync(Token));
        file.CompilerId = int.MaxValue;
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == id, Token));
        Assert.Equal(2, await db.Tests.CountAsync(t => t.ProblemId == id, Token));
        file.CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == id && s.IsReference, Token));
    }

    [Fact]
    public async Task SavedReferencesRunWithTheirCompilerOnPreviewTestsWithoutChangingVerdicts()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
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
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(Token);
        var manager = (CheckerManager)site.Services.GetRequiredService<ICheckerManager>();
        var runner = new ImportChecker(info);
        manager.Register(runner, Token);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { number = 1, name = "Run" }, true,
            ("A/solutions/main.cpp", "// original source"u8.ToArray())));
        var row = preview.Rows[0];
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        runner.Requests.Clear();
        var otherAuthor = await site.AddUserAsync();
        await db.Submissions.Where(s => s.ProblemId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, 2)
            .SetProperty(x => x.UserId, otherAuthor.Id).SetProperty(x => x.IsHidden, false), Token);
        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id, timeLimit = 7, inactiveTests = InactiveTestNumbers }));
        row = preview.Rows[0];
        var reference = Assert.Single(row.ReferenceSolutions);
        reference.Selected = false;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("// original source", request.Source);
        Assert.Equal(TimeSpan.FromSeconds(7), request.TimeLimit);
        Assert.Equal(ActiveTestNumbers, request.Tests.Select(t => t.Number));
        var run = Assert.Single(row.Runs);
        Assert.Equal(-1001, run.Result!.StatusCode);
        Assert.Equal(reference.Id, run.ReferenceId);
        Assert.Equal(2, await db.Submissions.Where(s => s.Id == reference.Id).Select(s => s.StatusCode).SingleAsync(Token));
        Assert.Equal(1, await db.CheckResults.CountAsync(r => r.SubmissionId == reference.Id, Token));
        Assert.Equal(1, await db.Problems.Where(p => p.Id == id).Select(p => p.TimeLimit).SingleAsync(Token));
        Assert.True(Assert.Single(row.ReferenceSolutions).Selected);
        row.TimeLimit++;
        Assert.False(row.ReferencesChecked);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        runner.FailChecks = true;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.False(row.ReferencesChecked);
        Assert.Null(Assert.Single(row.Runs).Result);
        await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(1, await db.Problems.Where(p => p.Id == id).Select(p => p.TimeLimit).SingleAsync(Token));
        runner.FailChecks = false;
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        using (var saveScope = site.Services.CreateScope())
            await saveScope.ServiceProvider.GetRequiredService<ContestImportService>().SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(-1001, await db.Submissions.Where(s => s.Id == reference.Id).Select(s => s.StatusCode).SingleAsync(Token));
        Assert.Equal(2, await db.CheckResults.CountAsync(r => r.SubmissionId == reference.Id, Token));
        Assert.Equal(8, await db.Problems.Where(p => p.Id == id).Select(p => p.TimeLimit).SingleAsync(Token));
        Assert.Equal(1, await manager.QueueRetestAsync(db.Submissions.Where(s => s.ProblemId == id), [], Token));
        var problems = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await db.Submissions.Where(s => s.ProblemId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, -1001), Token);
        Assert.Equal(1, await problems.RetestAsync(setup.Contest.Id, id, false, Token));
        await db.Submissions.Where(s => s.ProblemId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, -1), Token);
        Assert.Equal(1, await problems.RetestCompilationErrorsAsync(setup.Contest.Id, Token));
        Assert.Equal(2, await db.CheckResults.CountAsync(r => r.SubmissionId == reference.Id, Token));
        await db.Submissions.Where(s => s.ProblemId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCode, 1), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => problems.RetestAsync(setup.Contest.Id, id, false, Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => problems.RetestCompilationErrorsAsync(setup.Contest.Id, Token));
    }
}
