using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Olymp.Checker;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

namespace Olymp.WebApp.Services.Contests;

public sealed record ImportParameters(int Number, string Name, int TimeLimit, int SlowTimeLimit, int MemoryLimit, int CheckerId, bool IsActive);
public sealed record ImportTest(int Number, byte[] Input, byte[] Output, bool IsOpen, bool IsActive, int? Score, string? Comment);
public sealed record ImportRun(string File, string Compiler, CheckerResult? Result, string? Message, int? TimeLimit = null, int? ReferenceId = null, int? CompilerId = null);

public sealed class ImportSolution(ImportedSolution source)
{
    internal ImportedSolution Source { get; } = source;
    public string Filename => Source.Filename;
    public bool Selected { get; set; }
    public bool Save { get; set; }
    public int? CompilerId { get; set; }
    public IReadOnlyList<Compilator> Compilers { get; internal set; } = [];
    public bool AlreadySaved { get; internal set; }
    public IReadOnlyList<int> ReplacedIds { get; internal set; } = [];
}

public sealed class ContestImportRow
{
    internal ContestImportRow(ImportedProblem source, Problem? original)
    {
        Source = source;
        Original = original;
        Snapshot = ContestImportService.Snapshot(original);
        var data = source.Manifest;
        Parameters = new(data.Number ?? original?.Number ?? 0, data.Name ?? original?.Name ?? "",
            data.TimeLimit ?? original?.TimeLimit ?? 1, data.SlowTimeLimit ?? original?.SlowTimeLimit ?? data.TimeLimit ?? 1,
            data.MemoryLimit ?? original?.MemoryLimit ?? 256, data.CheckerId ?? original?.CheckerId ?? 4, data.IsActive ?? original?.IsActive ?? true);
        TimeLimit = Parameters.TimeLimit;
        SlowTimeLimit = Parameters.SlowTimeLimit;
        int open = data.OpenTestCount ?? (source.Tests.Count == 0 ? 0 : 1);
        Tests = [.. source.Tests.Select((test, i) => new ImportTest(test.Number, NormalizeLineEndings(test.Input), NormalizeLineEndings(test.Output), i < open,
            data.InactiveTests?.Contains(test.Number) != true,
            data.Scores is not null && data.Scores.TryGetValue(test.Number, out int score) ? score : null,
            data.TestComments?.GetValueOrDefault(test.Number)))];
        Solutions = [.. source.Solutions.OrderBy(s => s.Filename, StringComparer.Ordinal).Select(s => new ImportSolution(s))];
        Validator = source.Validator is { } validator ? new(new(ReferenceSolutionFiles.ValidatorFilename, validator)) : null;
    }

    internal ImportedProblem Source { get; }
    internal Problem? Original { get; set; }
    internal string Snapshot { get; set; }
    internal string? ValidatorKey { get; set; }
    internal string? ReferenceRunsKey { get; set; }
    internal ImportParameters? SavedParameters { get; set; }
    public string Folder => Source.Folder;
    public int? Id => Original?.Id ?? Source.Manifest.Id;
    public string Label => string.IsNullOrWhiteSpace(Parameters.Name) ? Folder : $"{Parameters.Number}. {Parameters.Name}";
    public ImportParameters Parameters { get; internal set; }
    public IReadOnlyList<ImportTest> Tests { get; }
    public IReadOnlyList<ImportSolution> Solutions { get; }
    public IReadOnlyList<ReferenceImportSolution> ReferenceSolutions { get; internal set; } = [];
    internal string? ReferenceSnapshot { get; set; }
    public ImportSolution? Validator { get; }
    public ReferenceImportSolution? SavedValidator { get; internal set; }
    internal IReadOnlyList<ReferenceImportSolution> ValidatorReferences { get; set; } = [];
    internal string? ValidatorSource => Validator?.Source.Source ?? SavedValidator?.Source.Source;
    public bool HasValidator => ValidatorSource is not null;
    public ValidatorResult? ValidatorResult { get; internal set; }
    public ValidatorResult? SavedValidatorResult { get; internal set; }
    public string? ValidatorMessage { get; internal set; }
    public bool ValidatorChecked => ValidatorKey is not null;
    public bool ReferencesChecked => IsSaved || (ReferenceSolutions.Count == 0 && !Solutions.Any(s => s.Save && !s.AlreadySaved))
        || ReferenceRunsKey == ContestImportService.ReferenceRunsKey(this);
    public bool ReferenceValidatorChecked => (Validator is not { Save: true, AlreadySaved: false } || ValidatorResult is not null)
        && (SavedValidator is null || SavedValidatorResult is not null);
    public bool IsSaved => SavedParameters == Proposed && !Solutions.Any(s => s.Save && !s.AlreadySaved)
        && Validator is not { Save: true, AlreadySaved: false };
    public int TimeLimit { get; set; }
    public int SlowTimeLimit { get; set; }
    public List<string> StateErrors { get; } = [];
    public List<string> StateWarnings { get; } = [];
    public List<string> ValidatorErrors { get; } = [];
    public List<string> ValidatorWarnings { get; } = [];
    public IReadOnlyList<string> Errors => [.. StateErrors, .. ValidatorErrors];
    public IReadOnlyList<string> Warnings => [.. StateWarnings, .. ValidatorWarnings];
    public List<ImportRun> Runs { get; } = [];
    public ImportParameters Proposed => Parameters with { TimeLimit = TimeLimit, SlowTimeLimit = SlowTimeLimit };
    public ImportParameters? Previous => Original is { } p ? new(p.Number, p.Name, p.TimeLimit, p.SlowTimeLimit, p.MemoryLimit, p.CheckerId, p.IsActive) : null;
    public IEnumerable<int> AddedTests => Tests.Where(t => Original?.Tests.Any(old => old.Number == t.Number) != true).Select(t => t.Number);
    public IEnumerable<int> RemovedTests => Original?.Tests.Where(old => Tests.All(t => t.Number != old.Number)).Select(t => t.Number) ?? [];
    public IEnumerable<int> ChangedTests => Tests.Where(t => Original?.Tests.Any(old => old.Number == t.Number && !SameTest(old, t)) == true).Select(t => t.Number);

    internal void CheckParameters()
    {
        StateErrors.Remove(SiteResources.ImportInvalidParameters);
        if ((!Id.HasValue && (Source.Manifest.Number is null || Source.Manifest.Name is null)) || Parameters.Number < 0
            || string.IsNullOrWhiteSpace(Parameters.Name) || TimeLimit <= 0 || SlowTimeLimit <= 0 || Parameters.MemoryLimit <= 0)
            StateErrors.Add(SiteResources.ImportInvalidParameters);
    }

    private static byte[] NormalizeLineEndings(byte[] source)
    {
        int extra = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
            else if (source[i] is (byte)'\r' or (byte)'\n') extra++;
        }
        if (extra == 0) return source;
        var result = new byte[source.Length + extra];
        int offset = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] is (byte)'\r' or (byte)'\n')
            {
                result[offset++] = (byte)'\r';
                result[offset++] = (byte)'\n';
                if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
            }
            else result[offset++] = source[i];
        }
        return result;
    }

    private static bool SameTest(Test old, ImportTest test) => old.Input.AsSpan().SequenceEqual(test.Input)
        && old.Output.AsSpan().SequenceEqual(test.Output) && old.IsActive == test.IsActive && old.IsOpen == test.IsOpen
        && old.Score == test.Score && old.Comment == test.Comment;
}

public sealed class ContestImportPreview
{
    internal ContestImportPreview(int contestId, IReadOnlyList<ContestImportRow> rows)
    {
        ContestId = contestId;
        Rows = rows;
    }
    public int ContestId { get; }
    public IReadOnlyList<ContestImportRow> Rows { get; }
    public string? ValidatorConfiguration { get; set; }
}

public sealed partial class ContestImportService(OlympContext context, ICheckerManager manager, IEnumerable<ISimpleChecker> checkers,
    IOptions<ContestImportOptions> options, IAuthorizationService authorization)
{
    private async Task AuthorizeAsync(ClaimsPrincipal actor, int contestId)
    {
        if (!(await authorization.AuthorizeAsync(actor, contestId, PolicyNames.ManageContest)).Succeeded)
            throw new OperationValidationException(SiteResources.StaffAccessDenied);
    }

    private static void EnsureMember(ContestImportPreview preview, ContestImportRow row)
    {
        if (!preview.Rows.Contains(row)) throw new ArgumentException("The problem is not in this preview.", nameof(row));
    }

    public async Task<ContestImportPreview> PrepareAsync(ClaimsPrincipal actor, int contestId, Stream zip, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, contestId);
        var package = await ProblemArchive.ReadAsync(zip, options.Value, token);
        if (!await context.Contests.AnyAsync(x => x.Id == contestId, token)) throw new EntityNotFoundException();
        if (package.ContestId is { } id && id != contestId) throw new OperationValidationException(SiteResources.ImportContestMismatch);
        int[] ids = [.. package.Problems.Where(p => p.Manifest.Id.HasValue).Select(p => p.Manifest.Id!.Value)];
        var originals = await context.Problems.AsNoTracking().Where(x => x.ContestId == contestId && ids.Contains(x.Id)).Include(x => x.Tests).ToListAsync(token);
        var preview = new ContestImportPreview(contestId, [.. package.Problems.Select(source => new ContestImportRow(source,
            originals.SingleOrDefault(p => p.Id == source.Manifest.Id))).OrderBy(r => r.Parameters.Number).ThenBy(r => r.Folder, StringComparer.Ordinal)]);
        foreach (var row in preview.Rows) await CheckStateAsync(preview, row, token);
        return preview;
    }

    public async Task<ContestImportRow> RefreshAsync(ClaimsPrincipal actor, ContestImportPreview preview, ContestImportRow row, CancellationToken token = default)
    {
        EnsureMember(preview, row);
        await AuthorizeAsync(actor, preview.ContestId);
        var original = await context.Problems.AsNoTracking().Where(x => x.Id == row.Id && x.ContestId == preview.ContestId).Include(x => x.Tests).SingleOrDefaultAsync(token);
        // A saved new problem must retain its assigned ID even if it was subsequently removed.
        if (row.Id.HasValue && original is null) throw new OperationValidationException(SiteResources.ImportUnknownId);
        var refreshed = new ContestImportRow(row.Source, original);
        if (row.TimeLimit == row.Parameters.TimeLimit) row.TimeLimit = refreshed.TimeLimit;
        if (row.SlowTimeLimit == row.Parameters.SlowTimeLimit) row.SlowTimeLimit = refreshed.SlowTimeLimit;
        if (row.Snapshot != Snapshot(original)) row.SavedParameters = null;
        row.Parameters = refreshed.Parameters;
        row.Original = original;
        row.Snapshot = Snapshot(original);
        await LoadReferenceSolutionsAsync(row, token);
        await CheckStateAsync(preview, row, token);
        return row;
    }

    internal static string Snapshot(Problem? p) => p is null ? "new" : Hash(new
    {
        p.Id,
        p.ContestId,
        p.Number,
        p.Name,
        p.TimeLimit,
        p.SlowTimeLimit,
        p.MemoryLimit,
        p.CheckerId,
        p.IsActive,
        p.Text,
        Tests = p.Tests.OrderBy(t => t.Id).Select(t => new { t.Id, t.Number, t.Input, t.Output, t.IsActive, t.IsOpen, t.Score, t.Comment })
    });
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public static string ConfigurationId(CompilatorInfo info) => Hash(CompilerConfigurationKey.From(info));

    public static IReadOnlyList<(ICheckerService Runner, CompilatorInfo Info)> AvailableConfigurations(ICheckerManager manager) =>
        [.. manager.Checkers.Values.Where(r => r.SelfTestingStatus == CheckerSelfTestingStatus.Success).OrderBy(r => r.Id)
            .SelectMany(r => (r.Compilators ?? []).Where(info => info.Error is null).Select(info => (Runner: r, Info: info)))
            .DistinctBy(c => CompilerConfigurationKey.From(c.Info)).OrderBy(c => c.Info.FullName, StringComparer.Ordinal)];

    private async Task CheckStateAsync(ContestImportPreview preview, ContestImportRow row, CancellationToken token)
    {
        row.StateErrors.Clear();
        row.StateWarnings.Clear();
        var original = await context.Problems.AsNoTracking().Where(x => x.ContestId == preview.ContestId && x.Id == row.Id).Include(x => x.Tests).SingleOrDefaultAsync(token);
        if (row.Snapshot != Snapshot(original)) throw new OperationValidationException(SiteResources.ImportStale);
        var m = row.Source.Manifest;
        if (row.Source.Error is { } error) row.StateErrors.Add(string.Format(SiteResources.ImportArchiveError, error));
        if (m.Id.HasValue && row.Original is null) row.StateErrors.Add(SiteResources.ImportUnknownId);
        row.CheckParameters();
        if (row.Proposed.IsActive && row.Tests.Count == 0) row.StateErrors.Add(SiteResources.ImportEmptyActive);
        if (m.OpenTests is not null) row.StateErrors.Add(SiteResources.ImportOpenTests);
        int open = m.OpenTestCount ?? (row.Tests.Count == 0 ? 0 : 1);
        if (open < 0 || open > row.Tests.Count || m.Scores?.Any(x => x.Value < 0 || row.Tests.All(t => t.Number != x.Key)) == true
            || m.Scores?.Values.Sum(x => (long)x) > int.MaxValue
            || m.InactiveTests?.Any(x => row.Tests.All(t => t.Number != x)) == true
            || m.TestComments?.Any(x => row.Tests.All(t => t.Number != x.Key)) == true)
            row.StateErrors.Add(SiteResources.ImportInvalidMetadata);
        if (m.Id.HasValue && preview.Rows.Count(x => x.Source.Manifest.Id == m.Id) > 1) row.StateErrors.Add(SiteResources.ImportDuplicateId);
        if (!checkers.Any(x => x.Id == row.Proposed.CheckerId) || !await context.Checkers.AnyAsync(x => x.Id == row.Proposed.CheckerId, token))
            row.StateErrors.Add(SiteResources.ImportUnsupportedChecker);
        if (await context.Problems.AnyAsync(p => p.ContestId == preview.ContestId && p.Id != row.Id && p.Number == row.Proposed.Number, token)
            || preview.Rows.Any(r => r != row && r.Parameters.Number == row.Parameters.Number))
            row.StateWarnings.Add(SiteResources.ImportDuplicateNumber);
        string? previous = row.ReferenceSnapshot;
        await LoadReferenceSolutionsAsync(row, token);
        if (previous is not null && previous != row.ReferenceSnapshot)
            throw new OperationValidationException(SiteResources.ImportReferencesChanged);
    }

    private (ICheckerService Runner, CompilatorInfo Info)? ValidatorConfiguration(ContestImportPreview preview) =>
        AvailableConfigurations(manager).Where(c => c.Info.Configuration.Language == "C++" && ConfigurationId(c.Info) == preview.ValidatorConfiguration)
            .Select(c => ((ICheckerService Runner, CompilatorInfo Info)?)c).FirstOrDefault();

    private static string ValidatorCacheKey(ContestImportPreview preview, ContestImportRow row) =>
        Hash(new { preview.ValidatorConfiguration, row.ValidatorSource, row.ReferenceSnapshot, row.Tests });

    internal static string ReferenceRunsKey(ContestImportRow row) => Hash(new
    {
        row.Proposed,
        row.Tests,
        row.ReferenceSnapshot,
        Files = row.Solutions.Where(s => s.Save && !s.AlreadySaved).Select(s => new { s.Filename, s.CompilerId })
    });

    public async Task EnsureValidatorAsync(ClaimsPrincipal actor, ContestImportPreview preview, ContestImportRow row, CancellationToken token = default)
    {
        EnsureMember(preview, row);
        await AuthorizeAsync(actor, preview.ContestId);
        await CheckStateAsync(preview, row, token);
        var runner = row.Validator is null && row.SavedValidator is { } storedValidator
            ? AvailableConfigurations(manager).Where(c => storedValidator.Submission.Compilator.IsActive
                && CompilerMatching.Matches(storedValidator.Submission.Compilator, c.Info))
                .Select(c => ((ICheckerService Runner, CompilatorInfo Info)?)c).FirstOrDefault()
            : ValidatorConfiguration(preview);
        if (row.Validator is { } file)
            file.CompilerId = runner is { } selected
                ? file.Compilers.FirstOrDefault(c => CompilerMatching.Matches(c, selected.Info))?.Id : null;
        string key = ValidatorCacheKey(preview, row);
        if (row.ValidatorKey == key && row.ReferenceValidatorChecked && (!row.HasValidator || row.ValidatorResult is not null || runner is null)) return;
        row.ValidatorKey = null;
        row.ValidatorResult = null;
        row.SavedValidatorResult = null;
        row.ValidatorErrors.Clear();
        row.ValidatorWarnings.Clear();
        row.ValidatorMessage = SiteResources.ImportValidatorNotRun;
        if (row.StateErrors.Count != 0) return;
        if (!row.HasValidator || runner is null)
        {
            row.ValidatorMessage = row.HasValidator ? SiteResources.ImportNoValidatorRunner : SiteResources.ImportNoValidator;
            row.ValidatorWarnings.Add(row.ValidatorMessage);
            row.ValidatorKey = key;
            return;
        }
        try
        {
            var result = await runner.Value.Runner.Validate(row.ValidatorSource!, runner.Value.Info,
                [.. row.Tests.Select(t => new ValidatorTest(t.Number, t.Input))], token);
            row.ValidatorResult = result;
            if (row.SavedValidator is { } saved)
            {
                if (saved.Source.Source == row.ValidatorSource && CompilerMatching.Matches(saved.Submission.Compilator, runner.Value.Info))
                    row.SavedValidatorResult = result;
                else
                {
                    var (savedRunner, savedInfo) = AvailableConfigurations(manager).FirstOrDefault(c => saved.Submission.Compilator.IsActive
                        && CompilerMatching.Matches(saved.Submission.Compilator, c.Info));
                    if (savedRunner is not null)
                        row.SavedValidatorResult = await savedRunner.Validate(saved.Source.Source, savedInfo,
                            [.. row.Tests.Select(t => new ValidatorTest(t.Number, t.Input))], token);
                }
            }
            if (!result.Compiled) row.ValidatorWarnings.Add(SiteResources.ImportValidatorCompileFailed);
            foreach (var test in result.Tests.Where(t => !t.IsValid))
            {
                var message = string.Format(SiteResources.ImportValidatorFailed, test.Number);
                if (row.Tests.Single(t => t.Number == test.Number).IsActive) row.ValidatorErrors.Add(message);
                else row.ValidatorWarnings.Add(message);
            }
            row.ValidatorMessage = !result.Compiled ? SiteResources.ImportValidatorCompileFailed
                : row.ValidatorErrors.Count != 0 ? SiteResources.ImportValidatorRejected : SiteResources.ImportValidatorPassed;
            row.ValidatorKey = key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.ValidatorMessage = SiteResources.ImportNoValidatorRunner;
            row.ValidatorWarnings.Add(row.ValidatorMessage);
            row.ValidatorKey = key;
        }
    }

    public async Task RunSolutionsAsync(ClaimsPrincipal actor, ContestImportPreview preview, ContestImportRow row,
        Func<Task>? resultAvailable = null, CancellationToken token = default)
    {
        EnsureMember(preview, row);
        await AuthorizeAsync(actor, preview.ContestId);
        await CheckStateAsync(preview, row, token);
        if (row.StateErrors.Count != 0) throw new OperationValidationException(SiteResources.ImportInvalidParameters);
        var configurations = AvailableConfigurations(manager);
        row.ReferenceRunsKey = null;
        var tests = row.Tests.Where(t => t.IsActive).Select(t => new CheckTest(t.Number, t.Input, t.Output, t.Score)).ToArray();
        int regular = row.TimeLimit, slow = row.SlowTimeLimit;
        var selected = row.Solutions.Where(s => s.Selected || (s.Save && !s.AlreadySaved))
            .Select(s => (s.Source, Compiler: s.Save && !s.AlreadySaved ? s.Compilers.FirstOrDefault(c => c.Id == s.CompilerId) : null,
                ReferenceId: (int?)null))
            .Concat(row.ReferenceSolutions.Select(s => (s.Source, Compiler: (Compilator?)s.Submission.Compilator, ReferenceId: (int?)s.Id))).ToArray();
        row.Runs.RemoveAll(run => selected.Any(s => s.Source.Filename == run.File && s.ReferenceId == run.ReferenceId));
        foreach (var (file, compiler, referenceId) in selected)
        {
            var extension = Path.GetExtension(file.Filename).ToLowerInvariant();
            var language = extension switch
            {
                ".cc" or ".cxx" or ".c++" => "C++",
                ".pp" => "Pascal",
                _ => configurations.FirstOrDefault(c => c.Info.Configuration.SourceExtension.Equals(extension, StringComparison.OrdinalIgnoreCase)).Info?.Configuration.Language
            };
            var matching = configurations.Where(c => compiler is null ? c.Info.Configuration.Language == language
                : compiler.IsActive && CompilerMatching.Matches(compiler, c.Info)).ToArray();
            if (matching.Length == 0 || tests.Length == 0)
            {
                row.Runs.Add(new(file.Filename, compiler?.Description ?? compiler?.Name ?? "", null,
                    tests.Length == 0 ? SiteResources.StaffNoTests : compiler is null ? SiteResources.ImportUnknownLanguage : SiteResources.CompilerUnavailable,
                    ReferenceId: referenceId));
                if (resultAvailable is not null) await resultAvailable();
                continue;
            }
            foreach (var (runner, info) in matching)
            {
                string label = info.FullName;
                int time = info.Configuration.UseSlowTimeLimit ? slow : regular;
                try
                {
                    var result = await runner.Check(new(Guid.NewGuid().ToString("N"), file.Source, info, row.Proposed.CheckerId,
                        tests, TimeSpan.FromSeconds(time), row.Proposed.MemoryLimit, true), token);
                    row.Runs.Add(new(file.Filename, label, result, null, time, referenceId, compiler?.Id));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row.Runs.Add(new(file.Filename, label, null, SiteResources.StaffRunnerDisconnected, time, referenceId));
                }
                if (resultAvailable is not null) await resultAvailable();
            }
        }
        if (row.ReferenceSolutions.All(s => row.Runs.Any(r => r.ReferenceId == s.Id && r.Result is not null))
            && row.Solutions.Where(s => s.Save && !s.AlreadySaved).All(s => row.Runs.Any(r => r.ReferenceId is null
                && r.File == s.Filename && r.CompilerId == s.CompilerId && r.Result is not null)))
            row.ReferenceRunsKey = ReferenceRunsKey(row);
    }

    public async Task<int> SaveAsync(ClaimsPrincipal actor, ContestImportPreview preview, ContestImportRow row, CancellationToken token = default)
    {
        EnsureMember(preview, row);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using (await manager.EnterAsync(token))
            {
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                await AuthorizeAsync(actor, preview.ContestId);
                await CheckStateAsync(preview, row, token);
                if (row.IsSaved) return row.Id!.Value;
                if (row.Errors.Count != 0 || row.ValidatorKey != ValidatorCacheKey(preview, row))
                    throw new OperationValidationException(SiteResources.ImportValidationRequired);
                ValidateReferenceSelection(row);
                if (!row.ReferencesChecked || !row.ReferenceValidatorChecked)
                    throw new OperationValidationException(SiteResources.ImportReferenceChecksRequired);
                if (!await context.Submissions.AnyAsync(s => s.ProblemId == row.Id && s.StatusCode == 1
                    && (s.Description == null || s.Description != ReferenceSolutionFiles.ValidatorFilename), token))
                {
                    var p = row.Id is { } id
                        ? await context.Problems.AsTracking().Where(p => p.Id == id && p.ContestId == preview.ContestId).Include(p => p.Tests).SingleAsync(token)
                        : new Problem { ContestId = preview.ContestId, Contest = null!, Name = "", Checker = null!, Tests = [], Submissions = [], Messages = [] };
                    if (row.Id is null) context.Problems.Add(p);
                    else { context.Tests.RemoveRange(p.Tests); p.Tests.Clear(); }
                    var data = row.Proposed;
                    p.Number = data.Number; p.Name = data.Name; p.TimeLimit = data.TimeLimit; p.SlowTimeLimit = data.SlowTimeLimit;
                    p.MemoryLimit = data.MemoryLimit; p.CheckerId = data.CheckerId; p.IsActive = data.IsActive;
                    foreach (var t in row.Tests)
                        p.Tests.Add(new Test
                        {
                            Problem = p,
                            Number = t.Number,
                            Input = t.Input,
                            Output = t.Output,
                            IsActive = t.IsActive,
                            IsOpen = t.IsOpen,
                            Score = t.Score,
                            Comment = t.Comment
                        });
                    await SaveReferenceSolutionsAsync(actor, row, p, token);
                    await context.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    row.Original = p;
                    row.Snapshot = Snapshot(p);
                    row.SavedParameters = data;
                    await LoadReferenceSolutionsAsync(row, token);
                    return p.Id;
                }
            }
            await Task.Delay(500, token);
        }
    }
}
