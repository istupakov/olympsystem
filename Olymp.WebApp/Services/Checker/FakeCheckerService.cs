using System.Text;

using Olymp.Checker;
using Olymp.Checker.Compilators;

namespace Olymp.WebApp.Services.Checker;

// Development only. Results are predefined; no source code is executed.
internal sealed class FakeCheckerService(Guid id, CheckerSelfTestingStatus status, IReadOnlyList<CompilatorInfo> compilers) : ICheckerService
{
    public Guid Id => id;
    public string Name => "Test Runner";
    public DateTimeOffset ConnectingTime { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ICompilatorConfiguration> Configurations { get; } = [.. compilers.Select(item => item.Configuration)];
    public IReadOnlyList<CompilatorInfo>? Compilators => compilers;
    public CheckerSelfTestingStatus SelfTestingStatus => status;
    public IEnumerable<CheckerTestResult> SelfTestingResults => compilers.Select(info => new CheckerTestResult("Version", info,
        CheckerResultStatus.Accepted, info.Error is null ? CheckerResultStatus.Accepted : CheckerResultStatus.InternalError,
        info.Error ?? "TEST RESULT: self-testing passed without executing code."));

    public Task<bool> RunSelfTests(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(status == CheckerSelfTestingStatus.Success);
    }

    public Task<CheckerResult> Check(CheckRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (status != CheckerSelfTestingStatus.Success || !compilers.Contains(request.Compilator))
            throw new InvalidOperationException("The test configuration is unavailable.");

        var log = new StringBuilder("TEST RESULT: predefined results, source code was not executed.\n");
        log.AppendLine($"Runner: {Name} ({Id})\nTime limit: {request.TimeLimit}\nMemory limit: {request.MemoryLimitMb} MB");
        int code = 2, score = 0;
        var results = new List<string>();

        foreach (var test in request.Tests)
        {
            token.ThrowIfCancellationRequested();
            var verdict = test.Number % 3 == 2 ? CheckerResultStatus.WrongAnswer : CheckerResultStatus.Accepted;
            log.AppendLine($"Test {test.Number}: {verdict} (TEST RESULT)");
            results.Add(verdict == CheckerResultStatus.Accepted ? (test.Score ?? 0).ToString() : verdict.ToString());

            if (verdict == CheckerResultStatus.Accepted) score += test.Score ?? 0;
            else
            {
                if (code == 2) code = verdict.MakeStatusCode(test.Number);
                if (!request.AllTests) break;
            }
        }

        bool scores = request.Tests.Any(test => test.Score.HasValue);
        return Task.FromResult(new CheckerResult(code, log.ToString(), scores ? score : null, scores ? string.Join(';', results) : null));
    }

    public Task<string> Run(string source, ICompilatorConfiguration configuration, string input, TimeSpan timeLimit, int memoryLimitMb, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!compilers.Any(info => info.Configuration == configuration && info.Error is null))
            throw new InvalidOperationException("The test configuration is unavailable.");

        return Task.FromResult($"TEST RESULT: predefined output, source code was not executed.\nRunner: {Name} ({Id})\nConfiguration: {configuration.Name}\nTime limit: {timeLimit}\nMemory limit: {memoryLimitMb} MB\nInput length: {input.Length}\nstdout:\n42\nstderr:\n");
    }

    public static IReadOnlyList<ICheckerService> Create()
    {
        var cpp = new GccCpp23Configuration().ParseInfo("14.2.0") with { DisplayName = "Test C++23 (GCC 14.2)", Description = "TEST CONFIGURATION: no compiler is executed." };
        var python = new CPythonConfiguration().ParseInfo("Python 3.13.1") with { DisplayName = "Test Python 3.13", Description = "TEST CONFIGURATION: no compiler is executed." };

        return [
            new FakeCheckerService(Guid.Parse("11111111-1111-4111-8111-111111111111"), CheckerSelfTestingStatus.Success, [cpp, python]),
            new FakeCheckerService(Guid.Parse("22222222-2222-4222-8222-222222222222"), CheckerSelfTestingStatus.Failed,
                [cpp, python with { Error = "TEST RESULT: python executable was not found." }])];
    }
}
