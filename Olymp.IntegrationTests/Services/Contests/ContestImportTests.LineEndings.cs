using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("\n", "\r\n")]
    [InlineData("\r", "\r\n")]
    [InlineData("\r\n", "\r\n")]
    [InlineData("a\nb\rc\r\nd\n\n", "a\r\nb\r\nc\r\nd\r\n\r\n")]
    [InlineData("last", "last")]
    public async Task TestsUseCrlfForPreviewValidationJudgingAndPersistenceWithoutRecoding(string raw, string normalized)
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        const string solution = "// solution\n// unchanged\n";
        const string validator = "// validator\n// unchanged\n";
        byte[] input = [0xff, 0, .. Encoding.UTF8.GetBytes(raw)];
        byte[] output = [0x80, 0, .. Encoding.UTF8.GetBytes(raw)];
        byte[] expectedInput = [0xff, 0, .. Encoding.UTF8.GetBytes(normalized)];
        byte[] expectedOutput = [0x80, 0, .. Encoding.UTF8.GetBytes(normalized)];
        byte[] Archive(object manifest) => Package(manifest, false,
            ("A/tests/input01.txt", input), ("A/tests/output01.txt", output),
            ("A/validator.cpp", Encoding.UTF8.GetBytes(validator)),
            ("A/solutions/main.cpp", Encoding.UTF8.GetBytes(solution)));
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Archive(new { number = 1, name = "CRLF" }));
        preview.ValidatorConfiguration = ContestImportService.ConfigurationId(Assert.Single(runner.Compilators!));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(expectedInput, Assert.Single(row.Tests).Input);
        Assert.Equal(expectedOutput, row.Tests[0].Output);
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        Assert.Equal(expectedInput, Assert.Single(runner.LastValidatorTests).Input.ToArray());
        Assert.Equal(validator, runner.LastValidatorSource);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(expectedInput, Assert.Single(request.Tests).Input.ToArray());
        Assert.Equal(expectedOutput, request.Tests[0].Output.ToArray());
        Assert.Equal(solution, request.Source);
        int id = await service.SaveAsync(setup.Actor, preview, row, Token);
        var stored = await db.Tests.SingleAsync(t => t.ProblemId == id, Token);
        Assert.Equal(expectedInput, stored.Input);
        Assert.Equal(expectedOutput, stored.Output);
        Assert.Equal(solution, await db.Submissions.Where(s => s.ProblemId == id).Select(s => s.Text).SingleAsync(Token));
        var repeated = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Archive(new { id }));
        Assert.Empty(Assert.Single(repeated.Rows).ChangedTests);
    }
}
