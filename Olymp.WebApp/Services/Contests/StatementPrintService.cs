using Olymp.Domain.Models;
using Olymp.Statements;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

internal static class StatementPrintService
{
    public static Task WriteAsync(Stream output, Contest contest, IEnumerable<Problem> problems, bool requireValid,
        CancellationToken token)
        => StatementTexArchive.WriteAsync(output, contest.Name, contest.StartTime, Prepare(problems, requireValid), SiteResources.StatementPrintWarning, token);

    internal static PrintableProblem[] Prepare(IEnumerable<Problem> problems, bool requireValid)
    {
        var printable = problems.Where(p => p.IsActive).OrderBy(p => p.Number).ThenBy(p => p.Id).Select(p =>
        {
            var document = p.Text is null ? null : StatementDocument.Parse(p.Text);
            if (requireValid && (string.IsNullOrWhiteSpace(p.Text) || document?.IsValid != true))
                throw new OperationValidationException($"{p.NameWithNumber}: {SiteResources.StatementPrintWarning}");
            return new PrintableProblem(p.Number, p.Name, p.TimeLimit, p.MemoryLimit, document,
                [.. p.Tests.Where(t => t.IsActive && t.IsOpen).OrderBy(t => t.Number).Select(t => new StatementExample(t.Input, t.Output))]);
        }).ToArray();
        if (printable.Length == 0) throw new OperationValidationException(SiteResources.NoProblemsFound);
        if (printable.Any(p => p.Number < 0) || printable.Select(p => p.Number).Distinct().Count() != printable.Length)
            throw new OperationValidationException(SiteResources.StatementProblemNumbers);
        return printable;
    }
}
