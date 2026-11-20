using System.Security.Claims;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Domain.Models;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Services.Contests;

public sealed class ReferenceImportSolution
{
    internal ReferenceImportSolution(Submission submission)
    {
        Submission = submission;
        Source = new(ReferenceSolutionFiles.Filename(submission.Id, submission.Description, submission.Compilator.SourceExtension), submission.Text);
    }

    internal Submission Submission { get; }
    internal ImportedSolution Source { get; }
    public int Id => Submission.Id;
    public string Filename => Source.Filename;
    public string Compiler => Submission.Compilator.Description ?? Submission.Compilator.Name;
    public int StatusCode => Submission.StatusCode;
    public int? CheckedStatus => Submission.CheckResults.OrderByDescending(check => check.CheckTime).ThenByDescending(check => check.Id)
        .Select(check => (int?)check.StatusCode).FirstOrDefault();
    public bool Selected { get; set; }
}

public sealed partial class ContestImportService
{
    private async Task LoadReferenceSolutionsAsync(ContestImportRow row, CancellationToken token)
    {
        row.RunDataKey = null;
        var submissions = row.Original is null ? [] : await context.Submissions.AsNoTracking()
            .Where(s => s.ProblemId == row.Id && s.IsReference).Include(s => s.Compilator)
            .Include(s => s.CheckResults.OrderByDescending(check => check.CheckTime).ThenByDescending(check => check.Id).Take(1))
            .OrderBy(s => s.Id).ToListAsync(token);
        row.ReferenceSnapshot = Hash(submissions.Select(s => new
        {
            s.Id,
            s.Description,
            s.SourceCode,
            s.CompilatorId,
            s.Compilator.IsActive,
            s.Compilator.Name,
            s.Compilator.ConfigName,
            s.Compilator.CommandLine,
            s.Compilator.SourceExtension
        }));
        var references = submissions.Select(s => new ReferenceImportSolution(s) { Selected = true }).ToArray();
        row.ReferenceSolutions = [.. references.Where(s => !ReferenceSolutionFiles.IsValidator(s.Filename))];
        row.ValidatorReferences = [.. references.Where(s => ReferenceSolutionFiles.IsValidator(s.Filename))];
        row.SavedValidator = row.ValidatorReferences.Count == 0 ? null : row.ValidatorReferences[^1];
        var compilers = await context.Compilators.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Id).ToListAsync(token);
        row.CompilerKeys = compilers.ToDictionary(compiler => compiler.Id, compiler => Hash(CompilerConfigurationKey.From(compiler)));
        foreach (var solution in ReferenceFiles(row))
        {
            string extension = Path.GetExtension(solution.Filename);
            string? language = extension.ToLowerInvariant() switch
            {
                ".cc" or ".cxx" or ".c++" => "C++",
                ".pp" => "Pascal",
                _ => compilers.FirstOrDefault(c => c.SourceExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))?.Language
            };
            solution.Compilers = [.. compilers.Where(c => c.Language == language)];
            var matching = references.Where(s => s.Filename.Equals(solution.Filename, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (solution.CompilerId is null)
            {
                int[] storedCompilers = [.. matching.Where(reference => reference.Source.Source == solution.Source.Source
                    && solution.Compilers.Any(compiler => compiler.Id == reference.Submission.CompilatorId))
                    .Select(reference => reference.Submission.CompilatorId).Distinct()];
                if (storedCompilers.Length == 1) solution.CompilerId = storedCompilers[0];
                else if (solution.Compilers.Count == 1) solution.CompilerId = solution.Compilers[0].Id;
            }
            solution.MatchingReferences = matching;
        }
    }

    private async Task SaveReferenceSolutionsAsync(ClaimsPrincipal actor, ContestImportRow row, Problem problem, CancellationToken token, bool validatorOnly = false)
    {
        var selected = ReferenceFiles(row).Where(s => s.Save && !s.AlreadySaved).ToArray();
        ValidateReferenceSelection(row);
        if (!int.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out int userId)
            || !await context.Users.AnyAsync(u => u.Id == userId, token))
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
        foreach (var solution in selected)
        {
            byte[] source = ReferenceSolutionFiles.SourceEncoding.GetBytes(solution.Source.Source);
            int[] replaced = [.. row.ReferenceSolutions.Concat(row.ValidatorReferences)
                .Where(s => s.Filename.Equals(solution.Filename, StringComparison.OrdinalIgnoreCase)).Select(s => s.Id)];
            if (replaced.Length != 0)
                await context.Submissions.Where(s => replaced.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsReference, false), token);
            var now = DateTimeOffset.UtcNow;
            var submission = new Submission
            {
                Problem = problem,
                User = null!,
                UserId = userId,
                Compilator = null!,
                CompilatorId = solution.CompilerId!.Value,
                Description = ReferenceSolutionFiles.IsValidator(solution.Filename) ? ReferenceSolutionFiles.ValidatorFilename : solution.Filename,
                SourceCode = source,
                IsReference = true,
                IsHidden = true,
                CommitTime = now,
                LastModification = now,
                CheckResults = []
            };
            var result = ReferenceSolutionFiles.IsValidator(solution.Filename) ? ValidatorCheckResult(row, row.ValidatorResult!)
                : row.Runs.Single(r => r.ReferenceId is null && r.File == solution.Filename && r.CompilerId == solution.CompilerId).Result!;
            ApplyReferenceResult(submission, result);
            context.Submissions.Add(submission);
        }
        if (validatorOnly) return;
        int[] replacedIds = [.. selected.SelectMany(s => row.ReferenceSolutions.Concat(row.ValidatorReferences)
            .Where(r => r.Filename.Equals(s.Filename, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id))];
        foreach (var reference in row.ReferencesToCheck.Where(r => !replacedIds.Contains(r.Id)))
        {
            var submission = await context.Submissions.AsTracking().SingleAsync(s => s.Id == reference.Id, token);
            await context.Entry(submission).ReloadAsync(token);
            ApplyReferenceResult(submission, row.Runs.Single(r => r.ReferenceId == reference.Id).Result!);
        }
        if (row.TestsChanged && row.SavedValidator is { } validator && !replacedIds.Contains(validator.Id))
        {
            var result = row.SavedValidatorResult ?? throw new OperationValidationException(SiteResources.ImportReferenceChecksRequired);
            var submission = await context.Submissions.AsTracking().SingleAsync(s => s.Id == validator.Id, token);
            await context.Entry(submission).ReloadAsync(token);
            submission.Description = ReferenceSolutionFiles.ValidatorFilename;
            ApplyReferenceResult(submission, ValidatorCheckResult(row, result));
        }
    }

    private static CheckerResult ValidatorCheckResult(ContestImportRow row, ValidatorResult result)
    {
        var failed = result.Tests.FirstOrDefault(t => !t.IsValid && row.Tests.Any(test => test.Number == t.Number && test.IsActive));
        return new(!result.Compiled ? CheckerResultStatus.CompilationError.MakeStatusCode()
            : failed is not null ? CheckerResultStatus.WrongAnswer.MakeStatusCode(failed.Number)
            : CheckerResultStatus.Accepted.MakeStatusCode(), result.Log);
    }

    private void ApplyReferenceResult(Submission submission, CheckerResult result)
    {
        var now = DateTimeOffset.UtcNow;
        submission.StatusCode = result.StatusCode;
        submission.Score = result.Score;
        submission.ScoresByTest = result.TestResults;
        submission.LastModification = now;
        context.CheckResults.Add(new()
        {
            Submission = submission,
            CheckTime = now,
            StatusCode = result.StatusCode,
            Log = result.Log,
            Score = result.Score,
            TestResults = result.TestResults
        });
    }

    internal static void ValidateReferenceSelection(ContestImportRow row)
    {
        foreach (var solution in row.Solutions.Where(s => s.Save))
            if (ReferenceSolutionFiles.IsValidator(solution.Filename))
                throw new OperationValidationException(SiteResources.ImportReservedValidatorName);
        foreach (var solution in ReferenceFiles(row).Where(s => s.Save && !s.AlreadySaved))
        {
            if (!ReferenceSolutionFiles.IsValidPath(solution.Filename))
                throw new OperationValidationException(string.Format(SiteResources.ImportInvalidPath, solution.Filename));
            if (solution.CompilerId is not { } compilerId || !solution.Compilers.Any(c => c.Id == compilerId))
                throw new OperationValidationException(string.Format(SiteResources.ImportSolutionCompilerRequired, solution.Filename));
            var error = new SourceCodeAttribute().GetValidationResult(solution.Source.Source, new(solution));
            if (error is not null) throw new OperationValidationException(error.ErrorMessage!);
            try { _ = ReferenceSolutionFiles.SourceEncoding.GetByteCount(solution.Source.Source); }
            catch (EncoderFallbackException) { throw new OperationValidationException(SiteResources.SourceCodeEncoding); }
        }
    }

    private static IEnumerable<ImportSolution> ReferenceFiles(ContestImportRow row) => row.Validator is { } validator
        ? row.Solutions.Append(validator) : row.Solutions;
}
