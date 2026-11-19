using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Tests.Shared;
using Olymp.WebApp.Services;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.IntegrationTests.Services.Contests;

public partial class ContestImportTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("\n", "\n")]
    [InlineData("\r", "\n")]
    [InlineData("\r\n", "\n")]
    [InlineData("a\nb\rc\r\nd\n\n", "a\nb\nc\nd\n\n")]
    [InlineData("a\r\r\nb\n\rc", "a\n\nb\n\nc")]
    [InlineData("last", "last")]
    public async Task TestsUseSelectedLineEndingsForPreviewValidationJudgingAndPersistenceWithoutRecoding(string raw, string lf)
    {
        foreach (var endings in Enum.GetValues<ImportLineEndings>())
        {
            string normalized = endings == ImportLineEndings.Crlf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
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
            var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, Archive(new { number = 1, name = "LF" }));
            Assert.Equal(ImportLineEndings.Crlf, preview.LineEndings);
            preview.LineEndings = endings;
            preview.ValidatorCompilerId = compiler.Id;
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
            repeated.LineEndings = endings;
            Assert.Empty(Assert.Single(repeated.Rows).ChangedTests);
            Assert.False(repeated.Rows[0].HasChanges);
        }
    }

    [Fact]
    public async Task SwitchingLineEndingsRebuildsEveryProblemFromOriginalBytesAndInvalidatesResults()
    {
        var setup = await SetupAsync();
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var service = scope.ServiceProvider.GetRequiredService<ContestImportService>();
        var (compiler, runner) = await AddReferenceCompilerAsync(db);
        var preview = await PrepareAsync(service, setup.Actor, setup.Contest.Id, ProblemArchives.Zip(
            ("A/problem.json", "{\"number\":1,\"name\":\"First\"}"u8.ToArray()),
            ("A/tests/input1.txt", "1\r\n2\r3\n"u8.ToArray()), ("A/tests/output1.txt", "1\n"u8.ToArray()),
            ("A/validator.cpp", "// validator"u8.ToArray()), ("A/solutions/main.cpp", "// source"u8.ToArray()),
            ("B/problem.json", "{\"number\":2,\"name\":\"Second\"}"u8.ToArray()),
            ("B/tests/input1.txt", "1\r\n2\r3\n"u8.ToArray()), ("B/tests/output1.txt", "1\n"u8.ToArray())));
        preview.ValidatorCompilerId = compiler.Id;
        var row = preview.Rows[0];
        row.Solutions[0].Save = true;
        row.Solutions[0].CompilerId = compiler.Id;
        await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
        await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        Assert.True(row.ValidatorChecked);
        Assert.True(row.ReferencesChecked);
        foreach (var endings in new[] { ImportLineEndings.Lf, ImportLineEndings.Crlf, ImportLineEndings.Lf })
        {
            preview.LineEndings = endings;
            string eol = endings == ImportLineEndings.Crlf ? "\r\n" : "\n";
            foreach (var problem in preview.Rows)
            {
                Assert.Equal(Encoding.UTF8.GetBytes($"1{eol}2{eol}3{eol}"), problem.Tests[0].Input);
                Assert.Equal(Encoding.UTF8.GetBytes($"1{eol}"), problem.Tests[0].Output);
                Assert.False(problem.ValidatorChecked);
                Assert.Null(problem.ValidatorResult);
                Assert.Empty(problem.Runs);
            }
            Assert.False(row.ReferencesChecked);
            await Assert.ThrowsAsync<OperationValidationException>(() => service.SaveAsync(setup.Actor, preview, row, Token));
            await service.EnsureValidatorAsync(setup.Actor, preview, row, Token);
            await service.RunSolutionsAsync(setup.Actor, preview, row, token: Token);
        }
    }
}
