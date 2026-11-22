using System.IO.Compression;

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
    [Fact]
    public async Task ValidatorsReplaceWithoutLosingHistoryAndProvideFallbackAndRootExport()
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
            Language = "C++",
            SourceExtension = info.Configuration.SourceExtension,
            ConfigName = info.Configuration.ConfigName,
            CommandLine = info.Configuration.CompileCommand,
            IsActive = true,
            Submissions = []
        };
        db.Compilators.Add(compiler);
        await db.SaveChangesAsync(Token);
        var runner = new ImportChecker(info);
        ((CheckerManager)site.Services.GetRequiredService<ICheckerManager>()).Register(runner, Token);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { number = 1, name = "Validator", inactiveTests = InactiveTestNumbers }, true,
                ("A/VALIDATOR.CPP", "// original validator"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var row = preview.Rows[0];
        row.Validator!.Save = true;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Equal(compiler.Id, row.Validator.CompilerId);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.True(row.IsSaved);
        Assert.True(row.Validator.AlreadySaved);
        Assert.Empty(row.ReferenceSolutions);
        int originalId = row.SavedValidator!.Id;
        Assert.Equal("validator.cpp", await db.Submissions.Where(s => s.Id == originalId).Select(s => s.Description).SingleAsync(Token));
        Assert.Equal(2, await db.Submissions.Where(s => s.Id == originalId).Select(s => s.StatusCode).SingleAsync(Token));
        db.CheckResults.Add(new() { Submission = null!, SubmissionId = originalId, CheckTime = DateTimeOffset.UtcNow, StatusCode = 2, Log = "saved history" });
        await db.SaveChangesAsync(Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == id, Token));

        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { id, inactiveTests = InactiveTestNumbers }, true, ("A/validator.cpp", "// replacement validator"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        row = preview.Rows[0];
        Assert.Equal([originalId], row.Validator!.ReplacedIds);
        row.Validator.Save = true;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.SaveAsync(setup.Actor, preview, row, Token);
        Assert.False(await db.Submissions.Where(s => s.Id == originalId).Select(s => s.IsReference).SingleAsync(Token));
        Assert.Equal(2, await db.CheckResults.CountAsync(r => r.SubmissionId == originalId, Token));
        Assert.True(await db.CheckResults.AnyAsync(r => r.SubmissionId == originalId && r.Log == "saved history", Token));
        Assert.Equal(2, await db.Submissions.CountAsync(s => s.ProblemId == id, Token));
        Assert.Equal(1, await db.Submissions.CountAsync(s => s.ProblemId == id && s.IsReference, Token));

        preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Package(new { id, inactiveTests = InactiveTestNumbers }));
        preview.ValidatorCompilerId = compiler.Id;
        row = preview.Rows[0];
        Assert.Null(row.Validator);
        Assert.True(row.HasValidator);
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Equal("// replacement validator", runner.LastValidatorSource);
        int calls = runner.ValidatorCalls;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Equal(calls, runner.ValidatorCalls);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.Empty(runner.Requests);
        using (var saveScope = site.Services.CreateScope())
            await saveScope.ServiceProvider.GetRequiredService<ContestImportService>().SaveAsync(setup.Actor, preview, row, Token);
        Assert.False(row.HasChanges);
        Assert.Equal(1, await db.CheckResults.CountAsync(r => r.SubmissionId == row.SavedValidator!.Id, Token));
        var export = await scope.ServiceProvider.GetRequiredService<ContestExportService>().ExportAsync(setup.Contest.Id, new(ReferenceSolutions: true), Token);
        await using var stream = export.Stream;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var validator = Assert.Single(archive.Entries, e => e.FullName.EndsWith("/validator.cpp", StringComparison.Ordinal));
        Assert.DoesNotContain("/solutions/", validator.FullName);
        using var reader = new StreamReader(validator.Open());
        Assert.Equal("// replacement validator", await reader.ReadToEndAsync(Token));
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("/solutions/", StringComparison.Ordinal));
        var manager = site.Services.GetRequiredService<ICheckerManager>();
        Assert.Equal(0, await manager.QueueRetestAsync(db.Submissions.Where(s => s.ProblemId == id), [], Token));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ContestProblemService>().RetestAsync(setup.Contest.Id, id, false, Token));
    }

    [Fact]
    public async Task ReservedSolutionNameCannotCreateOrReplaceAValidator()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id,
            Package(new { number = 1, name = "Reserved" }, true, ("A/solutions/validator.cpp", "// ordinary solution"u8.ToArray())));
        var row = preview.Rows[0];
        row.Solutions[0].Save = true;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        var error = await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
        Assert.Equal(SiteResources.ImportReservedValidatorName, error.Message);
        Assert.False(await scope.ServiceProvider.GetRequiredService<OlympContext>().Problems.AnyAsync(p => p.ContestId == setup.Contest.Id, Token));
    }
}
