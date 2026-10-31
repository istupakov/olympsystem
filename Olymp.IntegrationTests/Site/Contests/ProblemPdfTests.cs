using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Site.Contests;

[Collection("Contest management")]
public class ProblemPdfTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    [Theory]
    [InlineData("empty")]
    [InlineData("signature")]
    [InlineData("size")]
    public async Task InvalidPdfDoesNotReplaceExistingFile(string invalid)
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var original = "%PDF-1.4\nkeep"u8.ToArray();
        using var scope = site.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        await service.UploadPdfAsync(contest.Id, original, token);
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ContestImportOptions>>().Value;
        byte[] data = invalid switch
        {
            "empty" => [],
            "size" => new byte[options.MaxPdfBytes + 1],
            _ => "not a PDF"u8.ToArray()
        };
        if (invalid == "size")
            "%PDF-"u8.CopyTo(data);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.UploadPdfAsync(contest.Id, data, token));

        using var verification = site.Services.CreateScope();
        Assert.Equal(original, await verification.ServiceProvider.GetRequiredService<OlympContext>().Contests
            .Where(x => x.Id == contest.Id).Select(x => x.ProblemPdf!.Data).SingleAsync(token));
    }

    [Fact]
    public async Task PdfCanBeReplacedRemovedAndUploadedAgainInOneScope()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestProblemService>();
        var first = "%PDF-1.4\nfirst"u8.ToArray();
        var second = "%PDF-1.4\nsecond"u8.ToArray();

        await service.UploadPdfAsync(contest.Id, first, token);
        var originalId = await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.ProblemPdfId).SingleAsync(token);
        Assert.NotNull(originalId);

        await service.UploadPdfAsync(contest.Id, second, token);
        var replaced = await db.Contests.Include(x => x.ProblemPdf).SingleAsync(x => x.Id == contest.Id, token);
        Assert.Equal(originalId, replaced.ProblemPdfId);
        Assert.Equal(second, replaced.ProblemPdf!.Data);

        await service.RemovePdfAsync(contest.Id, token);
        await service.RemovePdfAsync(contest.Id, token);
        Assert.Null(await db.Contests.Where(x => x.Id == contest.Id).Select(x => x.ProblemPdfId).SingleAsync(token));
        Assert.False(await db.Blobs.AnyAsync(x => x.Id == originalId, token));

        await service.UploadPdfAsync(contest.Id, first, token);
        using var verification = site.Services.CreateScope();
        var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Contests
            .Include(x => x.ProblemPdf).SingleAsync(x => x.Id == contest.Id, token);
        Assert.NotEqual(originalId, saved.ProblemPdfId);
        Assert.Equal(first, saved.ProblemPdf!.Data);
    }

    [Fact]
    public async Task FailedBlobDeletionRollsBackContestPdfReference()
    {
        var token = TestContext.Current.CancellationToken;
        var contest = await site.AddContestAsync();
        var data = "%PDF-1.4\nkeep"u8.ToArray();
        int? blobId;
        using (var setup = site.Services.CreateScope())
        {
            await setup.ServiceProvider.GetRequiredService<ContestProblemService>().UploadPdfAsync(contest.Id, data, token);
            blobId = await setup.ServiceProvider.GetRequiredService<OlympContext>().Contests
                .Where(x => x.Id == contest.Id).Select(x => x.ProblemPdfId).SingleAsync(token);
        }

        using var failing = site.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OlympContext>(options => options.AddInterceptors(new FailBlobDeletion()))));
        using (var scope = failing.Services.CreateScope())
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<ContestProblemService>()
                .RemovePdfAsync(contest.Id, token));

        using var verification = site.Services.CreateScope();
        var saved = await verification.ServiceProvider.GetRequiredService<OlympContext>().Contests
            .Include(x => x.ProblemPdf).SingleAsync(x => x.Id == contest.Id, token);
        Assert.Equal(blobId, saved.ProblemPdfId);
        Assert.Equal(data, saved.ProblemPdf!.Data);
    }

    private sealed class FailBlobDeletion : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Blob>().Any(x => x.State == EntityState.Deleted))
                throw new DbUpdateException("Simulated blob deletion failure.");
            return ValueTask.FromResult(result);
        }
    }
}
