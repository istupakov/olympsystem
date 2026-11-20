using Microsoft.Extensions.Logging.Abstractions;

using Olymp.Tests.Shared;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public class StatementPdfTests
{
    [Fact]
    public async Task CompilationCanRecoverAfterTexErrorAndCancellation()
    {
        using var service = new StatementPdfService(NullLogger<StatementPdfService>.Instance);
        using var invalid = new MemoryStream(ProblemArchives.Zip(("statements.tex", "\\undefinedcommand"u8.ToArray())));
        await Assert.ThrowsAsync<OperationValidationException>(() => service.CompileAsync(invalid, TestContext.Current.CancellationToken));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        using var loop = new MemoryStream(ProblemArchives.Zip(("statements.tex", "\\documentclass{article}\\begin{document}\\loop\\iftrue\\repeat"u8.ToArray())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompileAsync(loop, timeout.Token));

        using var valid = new MemoryStream(ProblemArchives.Zip(("statements.tex", "\\documentclass{article}\\begin{document}Recovered\\end{document}"u8.ToArray())));
        byte[] data = await service.CompileAsync(valid, TestContext.Current.CancellationToken);
        Assert.True(data.AsSpan().StartsWith("%PDF-"u8));
    }
}
