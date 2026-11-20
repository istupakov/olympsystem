using System.Data;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Services.Contests;

public sealed class ValidatorImportCheck
{
    internal ValidatorImportCheck() { }
    internal ContestImportPreview Preview { get; init; } = default!;
    internal string Configuration { get; init; } = default!;
    internal ContestImportRow Row => Preview.Rows[0];
    public ValidatorResult Result => Row.ValidatorResult!;
    public IReadOnlyList<int> RejectedTests => [.. Result.Tests.Where(test => !test.IsValid).Select(test => test.Number)];
}

public sealed partial class ContestImportService
{
    public async Task<ValidatorImportCheck> CheckValidatorOnlyAsync(ClaimsPrincipal actor, ContestImportPreview preview,
        ContestImportRow row, CancellationToken token = default)
    {
        EnsureMember(preview, row);
        await AuthorizeAsync(actor, preview.ContestId);
        if (row.Validator is null || row.Id is null) throw new OperationValidationException(SiteResources.ImportUnknownId);
        var problem = await context.Problems.AsNoTracking().Include(problem => problem.Tests)
            .SingleOrDefaultAsync(problem => problem.Id == row.Id && problem.ContestId == preview.ContestId, token)
            ?? throw new OperationValidationException(SiteResources.ImportUnknownId);
        if (row.Snapshot != Snapshot(problem)) throw new OperationValidationException(SiteResources.ImportStale);
        var (runner, info) = await ValidatorConfigurationAsync(preview, token) ?? throw new OperationValidationException(SiteResources.ImportNoValidatorRunner);
        var source = new ImportedProblem(row.Folder, new ProblemManifest { Id = problem.Id }, [], row.Validator.Source.Source, []);
        var current = new ContestImportRow(source, problem)
        {
            Tests = [.. problem.Tests.OrderBy(test => test.Number).Select(test =>
                new ImportTest(test.Number, test.Input, test.Output, test.IsOpen, test.IsActive, test.Score, test.Comment))]
        };
        await LoadReferenceSolutionsAsync(current, token);
        current.Validator!.Save = true;
        current.Validator.CompilerId = preview.ValidatorCompilerId;
        ValidateReferenceSelection(current);
        current.ValidatorResult = await runner.Validate(current.ValidatorSource!, info,
            [.. current.Tests.Select(test => new ValidatorTest(test.Number, test.Input))], token);
        EnsureCompleteValidatorResult(current);
        return new ValidatorImportCheck
        {
            Preview = new(preview.ContestId, [current]),
            Configuration = ConfigurationId(info)
        };
    }

    public async Task SaveValidatorOnlyAsync(ClaimsPrincipal actor, ValidatorImportCheck check, CancellationToken token = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await AuthorizeAsync(actor, check.Preview.ContestId);
        var row = check.Row;
        await CheckStateAsync(check.Preview, row, token);
        ValidateReferenceSelection(row);
        EnsureCompleteValidatorResult(row);
        if (!AvailableConfigurations(manager).Any(configuration => ConfigurationId(configuration.Info) == check.Configuration
            && row.Validator!.Compilers.Any(compiler => compiler.Id == row.Validator.CompilerId && CompilerMatching.Matches(compiler, configuration.Info))))
            throw new OperationValidationException(SiteResources.ImportNoValidatorRunner);
        var problem = await context.Problems.AsTracking().SingleAsync(problem => problem.Id == row.Id && problem.ContestId == check.Preview.ContestId, token);
        await SaveReferenceSolutionsAsync(actor, row, problem, token, validatorOnly: true);
        await AuthorizeAsync(actor, check.Preview.ContestId);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private static void EnsureCompleteValidatorResult(ContestImportRow row)
    {
        if (row.ValidatorResult is not { Compiled: true } result)
            throw new OperationValidationException(SiteResources.ImportValidatorCompileFailed);
        if (!result.Tests.Select(test => test.Number).Order().SequenceEqual(row.Tests.Select(test => test.Number).Order()))
            throw new OperationValidationException(SiteResources.ImportValidationRequired);
    }
}
