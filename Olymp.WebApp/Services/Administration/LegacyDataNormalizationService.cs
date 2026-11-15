using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Administration;

public sealed class LegacyDataNormalizationService(OlympContext context, IAuthorizationService authorization)
{
    public sealed record Change(int Id, string Context, string Before, string After,
        string? DescriptionBefore = null, string? DescriptionAfter = null, string? Command = null, string? Configuration = null);

    public sealed record Preview(Change[] Participants, Change[] Compilers, Merge[] Merges, CompilerSnapshot[] EmptyCompilers,
        bool StandingOrderChanges, Skipped[] Skipped)
    {
        public int UnrecognizedCompilers => Skipped.Where(item => item.MergeSourceId is null).Select(item => item.Id).Distinct().Count();
        public int UnrecognizedPairs => Skipped.Where(item => item.MergeSourceId is not null).Select(item => item.MergeSourceId).Distinct().Count();
    }

    public sealed record Skipped(int Id, string Name, string Field, string? Expected, string? Actual,
        int? MergeSourceId = null, int? MergeDestinationId = null);

    public sealed record CompilerSnapshot(int Id, string Name, string? Description, string Language,
        string Extension, string Configuration, string Command, bool IsActive)
    {
        internal static CompilerSnapshot From(Compilator compiler) => new(compiler.Id, compiler.Name, compiler.Description,
            compiler.Language, compiler.SourceExtension, compiler.ConfigName, compiler.CommandLine, compiler.IsActive);
    }

    public sealed record Merge(CompilerSnapshot Source, CompilerSnapshot Destination, int[] SubmissionIds);

    private sealed record MergePreview(Merge[] Merges, Skipped[] Skipped);

    private async Task<MergePreview> PrepareMergeAsync(CancellationToken token)
    {
        var ids = LegacyCompilerNames.Merges.SelectMany(item => new[] { item.SourceId, item.DestinationId }).ToArray();
        var compilers = await context.Compilators.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var merges = new List<Merge>();
        var skipped = new List<Skipped>();

        foreach (var pair in LegacyCompilerNames.Merges)
        {
            if (!compilers.TryGetValue(pair.SourceId, out var source)) continue;
            var rule = LegacyCompilerNames.Rules.Single(item => item.Id == pair.SourceId);
            var differences = CompilerDifferences(source, rule);
            if (!compilers.TryGetValue(pair.DestinationId, out var destination))
            {
                differences.Add(new(pair.DestinationId, pair.Name, SiteResources.StaffNormalizationRecord,
                    SiteResources.StaffYes, SiteResources.StaffNo));
            }
            else
            {
                differences.AddRange(ConfigurationDifferences(destination, rule));
                AddDifference(differences, destination, SiteResources.StaffName, pair.Name, destination.Name);
                AddDifference(differences, destination, SiteResources.StaffDescription, pair.Description, destination.Description);
            }
            if (differences.Count > 0)
            {
                skipped.AddRange(differences.Select(item => item with { MergeSourceId = pair.SourceId, MergeDestinationId = pair.DestinationId }));
                continue;
            }

            var submissions = await context.Submissions.AsNoTracking().Where(item => item.CompilatorId == source.Id)
                .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(token);
            merges.Add(new(CompilerSnapshot.From(source), CompilerSnapshot.From(destination!), submissions));
        }

        return new([.. merges], [.. skipped]);
    }

    public async Task<Preview> PrepareAsync(ClaimsPrincipal actor, CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        var (participantChanges, standingOrderChanges) = await PrepareParticipantsAsync(token);
        var (compilerChanges, compilerSkipped) = await PrepareCompilersAsync(token);
        var merge = await PrepareMergeAsync(token);
        var destinationIds = LegacyCompilerNames.Merges.Select(item => item.DestinationId).ToArray();
        var empty = await context.Compilators.AsNoTracking()
            .Where(item => !item.IsActive && !item.Submissions.Any() && !destinationIds.Contains(item.Id))
            .OrderBy(item => item.Id).ToListAsync(token);
        var removedIds = merge.Merges.Select(item => item.Source.Id).Concat(empty.Select(item => item.Id)).ToHashSet();

        return new(participantChanges, [.. compilerChanges.Where(item => !removedIds.Contains(item.Id))],
            merge.Merges, [.. empty.Where(item => !merge.Merges.Any(pair => pair.Source.Id == item.Id)).Select(CompilerSnapshot.From)],
            standingOrderChanges, [.. compilerSkipped, .. merge.Skipped]);
    }

    public async Task<int> ApplyAsync(ClaimsPrincipal actor, Preview preview, CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await PrepareAsync(actor, token);

        if (!current.Participants.SequenceEqual(preview.Participants) || !current.Compilers.SequenceEqual(preview.Compilers)
            || !current.EmptyCompilers.SequenceEqual(preview.EmptyCompilers)
            || current.StandingOrderChanges != preview.StandingOrderChanges
            || !current.Skipped.SequenceEqual(preview.Skipped)
            || current.Merges.Length != preview.Merges.Length
            || current.Merges.Where((merge, index) => merge.Source != preview.Merges[index].Source
                || merge.Destination != preview.Merges[index].Destination
                || !merge.SubmissionIds.SequenceEqual(preview.Merges[index].SubmissionIds)).Any())
            throw new OperationValidationException(SiteResources.StaffNormalizationChanged);

        var participantIds = current.Participants.Select(item => item.Id).ToArray();
        var participants = await context.Competitors.AsTracking().Where(item => participantIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        foreach (var change in current.Participants) participants[change.Id].Name = change.After;

        var compilerIds = current.Compilers.Select(item => item.Id).ToArray();
        var compilers = await context.Compilators.AsTracking().Where(item => compilerIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        foreach (var change in current.Compilers)
        {
            compilers[change.Id].Name = change.After;
            compilers[change.Id].Description = change.DescriptionAfter;
        }
        await context.SaveChangesAsync(token);

        foreach (var merge in current.Merges)
        {
            var moved = await context.Submissions.Where(item => item.CompilatorId == merge.Source.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CompilatorId, merge.Destination.Id), token);
            if (moved != merge.SubmissionIds.Length) throw new OperationValidationException(SiteResources.StaffNormalizationChanged);

            var deleted = await context.Compilators.Where(item => item.Id == merge.Source.Id).ExecuteDeleteAsync(token);
            if (deleted != 1) throw new OperationValidationException(SiteResources.StaffNormalizationChanged);
        }

        var emptyIds = current.EmptyCompilers.Select(item => item.Id).ToArray();
        var emptyDeleted = await context.Compilators.Where(item => emptyIds.Contains(item.Id) && !item.IsActive && !item.Submissions.Any())
            .ExecuteDeleteAsync(token);
        if (emptyDeleted != emptyIds.Length) throw new OperationValidationException(SiteResources.StaffNormalizationChanged);

        await transaction.CommitAsync(token);
        return current.Participants.Length + current.Compilers.Length + current.Merges.Length + emptyDeleted;
    }

    private async Task<(Change[] Changes, Skipped[] Skipped)> PrepareCompilersAsync(CancellationToken token)
    {
        var ids = LegacyCompilerNames.Rules.Select(item => item.Id).ToArray();
        var compilers = await context.Compilators.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var changes = new List<Change>();
        var skipped = new List<Skipped>();

        foreach (var rule in LegacyCompilerNames.Rules)
        {
            if (!compilers.TryGetValue(rule.Id, out var compiler)) continue;
            if (!compiler.IsActive && compiler.Name == rule.Name && compiler.Description == rule.Description
                && ConfigurationMatches(compiler, rule)) continue;

            var differences = CompilerDifferences(compiler, rule);
            if (differences.Count > 0)
            {
                skipped.AddRange(differences);
                continue;
            }

            changes.Add(new(compiler.Id, compiler.Language, compiler.Name, rule.Name,
                compiler.Description, rule.Description, compiler.CommandLine, compiler.ConfigName));
        }

        return ([.. changes], [.. skipped]);
    }

    private static List<Skipped> CompilerDifferences(Compilator compiler, LegacyCompilerNames.Rule rule)
    {
        var differences = ConfigurationDifferences(compiler, rule);
        if (compiler.IsActive)
            differences.Add(new(compiler.Id, compiler.Name, SiteResources.StaffActive, SiteResources.StaffNo, SiteResources.StaffYes));

        var normalized = compiler.Description is not null;
        AddDifference(differences, compiler, SiteResources.StaffName, normalized ? rule.Name : rule.OldName, compiler.Name);
        AddDifference(differences, compiler, SiteResources.StaffDescription, normalized ? rule.Description : null, compiler.Description);
        return differences;
    }

    private static List<Skipped> ConfigurationDifferences(Compilator compiler, LegacyCompilerNames.Rule rule)
    {
        var differences = new List<Skipped>();
        AddDifference(differences, compiler, SiteResources.StaffLanguage, rule.Language, compiler.Language);
        AddDifference(differences, compiler, SiteResources.StaffSourceExtension, rule.Extension, compiler.SourceExtension);
        AddDifference(differences, compiler, SiteResources.StaffConfiguration, rule.Configuration, compiler.ConfigName);
        AddDifference(differences, compiler, SiteResources.StaffCommandLine, rule.Command, compiler.CommandLine);
        return differences;
    }

    private static void AddDifference(List<Skipped> differences, Compilator compiler, string field, string? expected, string? actual)
    {
        if (expected != actual) differences.Add(new(compiler.Id, compiler.Name, field, expected, actual));
    }

    private static bool ConfigurationMatches(Compilator compiler, LegacyCompilerNames.Rule rule) =>
        compiler.Language == rule.Language && compiler.SourceExtension == rule.Extension
        && compiler.ConfigName == rule.Configuration && compiler.CommandLine == rule.Command;

    private async Task<(Change[] Changes, bool StandingOrderChanges)> PrepareParticipantsAsync(CancellationToken token)
    {
        int[] ids = [1, 3, 14];
        var contests = await context.Contests.AsNoTrackingWithIdentityResolution()
            .Where(item => ids.Contains(item.Id) && item.StartTime.Year == 2010)
            .Include(item => item.Competitors)
            .Include(item => item.Problems).ThenInclude(item => item.Submissions).ThenInclude(item => item.User)
            .AsSplitQuery().OrderBy(item => item.Id).ToListAsync(token);
        var changes = new List<Change>();
        var orderChanges = false;
        var now = DateTimeOffset.UtcNow;

        foreach (var contest in contests)
        {
            if (contest.IsAcm) continue;

            var replacements = contest.Competitors.Select(item => new Change(item.Id, contest.Name, item.Name,
                NormalizeParticipantName(contest.Id, item.Name))).Where(item => item.Before != item.After).OrderBy(item => item.Id).ToArray();
            if (replacements.Length == 0) continue;

            changes.AddRange(replacements);
            var before = StandingOrders(contest, now);
            var names = replacements.ToDictionary(item => item.Id);
            foreach (var participant in contest.Competitors)
                if (names.TryGetValue(participant.Id, out var replacement)) participant.Name = replacement.After;

            var after = StandingOrders(contest, now);
            orderChanges |= before.Where((order, index) => !order.SequenceEqual(after[index])).Any();
        }

        return ([.. changes], orderChanges);
    }

    private static int[][] StandingOrders(Contest contest, DateTimeOffset now)
    {
        int[] Order(bool official, bool notFreeze) => [.. Standing.NewStanding(contest, !official, official, notFreeze, null, now)
            .Select(item => item.User.Id)];

        return [Order(false, false), Order(false, true), Order(true, false), Order(true, true)];
    }

    internal static string NormalizeParticipantName(int contestId, string name)
    {
        var pattern = contestId switch
        {
            1 => @"^(?<name>[^,]+?\.)\s+(?<info>(?:ФПМИ|АВТФ),\s*[^,]+?)\s*$",
            3 => @"^(?<name>[^,()]+)\s+\((?<info>НГУ|НГТУ|СибГУТИ|СГУПС|НГПУ)\)\s*$",
            14 => @"^(?<name>[^,]+?\.)\s+(?<info>ПМИ?-0[1-4])\s*$",
            _ => null
        };
        if (pattern is null) return name;

        var match = Regex.Match(name, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? $"{match.Groups["name"].Value.Trim()}, {match.Groups["info"].Value.Trim()}" : name;
    }

    private async Task AuthorizeAsync(ClaimsPrincipal actor)
    {
        if (!(await authorization.AuthorizeAsync(actor, PolicyNames.Admin)).Succeeded) throw new UnauthorizedAccessException();
    }
}
