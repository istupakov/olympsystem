using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Statements;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public sealed record MaterialChange(string Name, string? Before, string? After, bool IsPdf = false)
{
    public bool IsDeletion => !IsPdf && Before is not null && After is null;
}

public sealed class MaterialImportRow
{
    internal MaterialImportRow(ContestMaterials source) => Source = source;
    internal ContestMaterials Source { get; }
    internal string? Snapshot { get; set; }
    internal byte[]? Pdf { get; set; }
    public int? ContestId => Source.ContestId;
    public string Name { get; internal set; } = "";
    public IReadOnlyList<MaterialChange> Changes { get; internal set; } = [];
    public string? Error { get; internal set; }
    public bool Saved { get; internal set; }
    public bool CanSave => !Saved && Error is null && Changes.Count != 0;
}

public sealed class MaterialImportService(IDbContextFactory<OlympContext> factory, IAuthorizationService authorization,
    StatementPdfService pdf, IOptions<ContestImportOptions> options)
{
    public async Task<IReadOnlyList<MaterialImportRow>> PreviewAsync(ClaimsPrincipal actor, Stream archive, bool regeneratePdf,
        CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        var materials = await MaterialArchive.ReadAsync(archive, options.Value, token);
        var rows = new List<MaterialImportRow>();
        foreach (var source in materials)
        {
            token.ThrowIfCancellationRequested();
            var row = new MaterialImportRow(source) { Name = source.Folder, Error = source.Error };
            rows.Add(row);
            if (row.Error is not null) continue;
            try
            {
                await using var db = await factory.CreateDbContextAsync(token);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var contest = await LoadAsync(db, source, regeneratePdf, false, token)
                    ?? throw new OperationValidationException(SiteResources.ImportUnknownId);
                row.Name = contest.Name;
                row.Snapshot = Snapshot(contest, source, regeneratePdf);
                var changes = PrepareChanges(contest, source);
                Apply(contest, source);
                await transaction.CommitAsync(token);
                if (regeneratePdf)
                {
                    using var tex = new MemoryStream();
                    await StatementPrintService.WriteAsync(tex, contest, contest.Problems, true, token);
                    tex.Position = 0;
                    row.Pdf = await pdf.CompileAsync(tex, token);
                    if (row.Pdf.LongLength > options.Value.MaxPdfBytes)
                        throw new OperationValidationException(string.Format(SiteResources.ImportFileLimit, "statements.pdf"));
                    changes.Add(new(SiteResources.Problems, null, null, IsPdf: true));
                }
                row.Changes = changes;
            }
            catch (Exception exception) when (exception is OperationValidationException or EncoderFallbackException)
            {
                row.Error = exception is EncoderFallbackException ? SiteResources.MaterialTableEncoding : exception.Message;
                row.Pdf = null;
            }
        }
        return rows;
    }

    public async Task SaveAsync(ClaimsPrincipal actor, MaterialImportRow preview, CancellationToken token = default)
    {
        await AuthorizeAsync(actor);
        if (!preview.CanSave || preview.Snapshot is null) throw new OperationValidationException(SiteResources.ImportValidationRequired);
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        bool regeneratePdf = preview.Pdf is not null;
        var contest = await LoadAsync(db, preview.Source, regeneratePdf, true, token);
        if (contest is null || Snapshot(contest, preview.Source, regeneratePdf) != preview.Snapshot)
            throw new OperationValidationException(SiteResources.ImportStale);
        var previousTables = new[] { contest.FinalTable, contest.OfficialTable }.OfType<Blob>().ToArray();
        Apply(contest, preview.Source);
        if (preview.Pdf is { } bytes)
        {
            if (contest.ProblemPdf is null) contest.ProblemPdf = new Blob { Data = bytes };
            else contest.ProblemPdf.Data = bytes;
        }
        await AuthorizeAsync(actor);
        await db.SaveChangesAsync(token);
        foreach (var old in previousTables.Where(b => b.Id != contest.FinalTableId && b.Id != contest.OfficialTableId).DistinctBy(b => b.Id))
        {
            if (!await db.Contests.AnyAsync(c => c.FinalTableId == old.Id || c.OfficialTableId == old.Id || c.ProblemPdfId == old.Id || c.TestZipId == old.Id, token))
                db.Blobs.Remove(old);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        preview.Saved = true;
        preview.Pdf = null;
    }

    private async Task AuthorizeAsync(ClaimsPrincipal actor)
    {
        if (!(await authorization.AuthorizeAsync(actor, PolicyNames.Admin)).Succeeded)
            throw new UnauthorizedAccessException();
    }

    private static async Task<Contest?> LoadAsync(OlympContext db, ContestMaterials source, bool includeExamples, bool tracking, CancellationToken token)
    {
        var query = db.Contests.Where(c => c.Id == source.ContestId).Include(c => c.FinalTable).Include(c => c.OfficialTable).Include(c => c.ProblemPdf);
        IQueryable<Contest> complete = includeExamples
            ? query.Include(c => c.Problems).ThenInclude(p => p.Tests.Where(t => t.IsActive && t.IsOpen))
            : query.Include(c => c.Problems);
        if (source.Participants.Count != 0) complete = complete.Include(c => c.Competitors);
        return await (tracking ? complete.AsTracking() : complete.AsNoTracking()).AsSplitQuery().SingleOrDefaultAsync(token);
    }

    private static string Snapshot(Contest contest, ContestMaterials source, bool includeExamples)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contest.Name,
            contest.StartTime,
            contest.Description,
            contest.FinalTableId,
            contest.OfficialTableId,
            contest.ProblemPdfId,
            Final = contest.FinalTable?.Data,
            Official = contest.OfficialTable?.Data,
            Pdf = contest.ProblemPdf?.Data,
            Participants = source.Participants.Count == 0 ? null : contest.Competitors.OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.Name, p.IsApproved, p.IsOutOfCompetition, p.IsDisqualified }),
            Problems = contest.Problems.OrderBy(p => p.Id).Select(p => new
            {
                p.Id,
                p.Text,
                p.Number,
                p.Name,
                p.TimeLimit,
                p.MemoryLimit,
                p.IsActive,
                Tests = includeExamples ? p.Tests.OrderBy(t => t.Id).Select(t => new { t.Id, t.Number, t.Input, t.Output }) : null
            })
        });
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private static List<MaterialChange> PrepareChanges(Contest contest, ContestMaterials source)
    {
        var changes = new List<MaterialChange>();
        if (source.Name is { } name && contest.Name != name)
            changes.Add(new(SiteResources.StaffContest + " · " + SiteResources.StaffName, contest.Name, name));
        foreach (var (path, text) in source.Files)
        {
            string? before = path.ToLowerInvariant() switch
            {
                "description.html" => contest.Description,
                "tables/final.html" => contest.FinalTableText,
                "tables/official.html" => contest.OfficialTableText,
                _ => throw new InvalidDataException()
            };
            string? after = string.IsNullOrWhiteSpace(text) ? null : text;
            if (path.StartsWith("tables/", StringComparison.OrdinalIgnoreCase) && after is not null)
                TableBytes(after);
            if (before != after) changes.Add(new(path, before, after));
        }
        foreach (var (id, text) in source.Statements)
        {
            var problem = contest.Problems.SingleOrDefault(p => p.Id == id)
                ?? throw new OperationValidationException($"ID {id}: {SiteResources.MaterialProblemOwnership}");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var document = StatementDocument.Parse(text);
                if (!document.IsValid)
                    throw new OperationValidationException($"{problem.NameWithNumber}: {string.Join("; ", document.Diagnostics.Select(StatementMessages.Diagnostic))}");
                if (problem.Name != document.Title)
                    changes.Add(new($"{problem.NameWithNumber} · ID {id} · {SiteResources.StaffName}", problem.Name, document.Title));
            }
            string? after = string.IsNullOrWhiteSpace(text) ? null : text;
            if (problem.Text != after) changes.Add(new($"{problem.NameWithNumber} · ID {id}", problem.Text, after));
        }
        var renamedParticipants = source.Participants.Where(p => p.Name is not null).ToDictionary(p => p.Id, p => p.Name!);
        var finalParticipantNames = source.Participants.Count == 0 ? null : contest.Competitors.ToLookup(
            p => renamedParticipants.GetValueOrDefault(p.Id) ?? p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in source.Participants)
        {
            var participant = contest.Competitors.SingleOrDefault(p => p.Id == item.Id)
                ?? throw new OperationValidationException($"ID {item.Id}: {SiteResources.MaterialParticipantOwnership}");
            string label = $"{participant.Name} · ID {item.Id}";
            if (item.Name is { } participantName && participantName != participant.Name)
            {
                if (finalParticipantNames![participantName].Any(p => p.Id != item.Id))
                    throw new OperationValidationException(SiteResources.TeamNameAlreadyRegistered);
                changes.Add(new($"{label} · {SiteResources.StaffName}", participant.Name, participantName));
            }
            AddFlag(SiteResources.StaffApproval, participant.IsApproved, item.IsApproved);
            AddFlag(SiteResources.StaffUnofficial, participant.IsOutOfCompetition, item.IsOutOfCompetition);
            AddFlag(SiteResources.StaffDisqualified, participant.IsDisqualified, item.IsDisqualified);

            void AddFlag(string field, bool before, bool? after)
            {
                if (after is bool value && value != before)
                    changes.Add(new($"{label} · {field}", before ? SiteResources.StaffYes : SiteResources.StaffNo,
                        value ? SiteResources.StaffYes : SiteResources.StaffNo));
            }
        }
        return changes;
    }

    private static void Apply(Contest contest, ContestMaterials source)
    {
        if (source.Name is { } name) contest.Name = name;
        foreach (var (path, text) in source.Files)
        {
            string? after = string.IsNullOrWhiteSpace(text) ? null : text;
            switch (path.ToLowerInvariant())
            {
                case "description.html": contest.Description = after; break;
                case "tables/final.html":
                    contest.FinalTable = SetTable(contest.FinalTable, after);
                    break;
                case "tables/official.html":
                    contest.OfficialTable = SetTable(contest.OfficialTable, after);
                    break;
            }
        }
        foreach (var (id, text) in source.Statements)
        {
            var problem = contest.Problems.Single(p => p.Id == id);
            problem.Text = string.IsNullOrWhiteSpace(text) ? null : text;
            if (problem.Text is not null) problem.Name = StatementDocument.Parse(problem.Text).Title!;
        }
        foreach (var item in source.Participants)
        {
            var participant = contest.Competitors.Single(p => p.Id == item.Id);
            if (item.Name is { } participantName) participant.Name = participantName;
            if (item.IsApproved is bool approved) participant.IsApproved = approved;
            if (item.IsOutOfCompetition is bool unofficial) participant.IsOutOfCompetition = unofficial;
            if (item.IsDisqualified is bool disqualified) participant.IsDisqualified = disqualified;
        }
    }

    private static byte[] TableBytes(string text) => Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(text);

    private static Blob? SetTable(Blob? table, string? text)
    {
        if (text is null) return null;
        if (table is null) return new Blob { Data = TableBytes(text) };
        table.Data = TableBytes(text);
        return table;
    }
}
