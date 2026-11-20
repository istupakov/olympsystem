using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Olymp.Statements;

public sealed record StatementExample(byte[] Input, byte[] Output);
public sealed record PrintableProblem(int Number, string Name, int TimeLimit, int MemoryLimit,
    StatementDocument? Document, IReadOnlyList<StatementExample> Examples);

public static class StatementTexArchive
{
    public static async Task WriteAsync(Stream destination, string contestName, DateTimeOffset start,
        IReadOnlyList<PrintableProblem> problems, string unavailable, CancellationToken token = default)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var assembly = typeof(StatementTexArchive).Assembly;
        using var preamble = new StreamReader(assembly.GetManifestResourceStream("Olymp.Statements.Latex.preamble.tex")
            ?? throw new InvalidOperationException("Missing preamble resource."));
        var main = new StringBuilder((await preamble.ReadToEndAsync(token))
            .Replace("STATEMENT_NOTE_HEADING", StatementDocument.NoteHeading, StringComparison.Ordinal));
        main.Append("\n\\contest{").Append(StatementDocument.Escape(contestName, true)).Append("}{}{")
            .Append(start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append("}\n\\begin{document}\n\\raggedbottom\n");
        var warnings = new List<string>();
        foreach (var (index, problem) in problems.OrderBy(p => p.Number).Index())
        {
            // Sequence-based file names also support duplicate problem numbers.
            string stem = "problem" + index.ToString("D2", CultureInfo.InvariantCulture);
            string title = problem.Document is { IsValid: true } valid ? valid.Title! : problem.Name;
            var text = new StringBuilder("\\setcounter{problem}{").Append(problem.Number - 1).Append("}\n\\begin{problem}{")
                .Append(StatementDocument.Escape(title, true)).Append("}{}{}{").Append(problem.TimeLimit)
                .Append(" с}{").Append(problem.MemoryLimit).Append(" MiB}{}\n");
            if (problem.Document is { IsValid: true } document) text.Append(document.Latex());
            else
            {
                text.Append(StatementDocument.Escape(unavailable, true)).Append('\n');
                warnings.Add($"{problem.Number}. {problem.Name}: {unavailable}");
            }
            if (problem.Examples.Count != 0)
            {
                text.Append("\\Examples\n\\begin{example}\n");
                foreach (var (exampleIndex, example) in problem.Examples.Index())
                {
                    string input = $"samples/{stem}-{exampleIndex + 1}.in", output = $"samples/{stem}-{exampleIndex + 1}.out";
                    await WriteBytesAsync(archive, input, example.Input, token);
                    await WriteBytesAsync(archive, output, example.Output, token);
                    text.Append("\\exmpfile{").Append(input).Append("}{").Append(output).Append("}\n");
                }
                text.Append("\\end{example}\n");
            }
            if (problem.Document is { IsValid: true } notes) text.Append(notes.Latex(notesOnly: true));
            text.Append("\\end{problem}\n");
            await WriteTextAsync(archive, stem + ".tex", text.ToString(), token);
            main.Append("\\input{").Append(stem).Append(".tex}\n");
        }
        main.Append("\\end{document}\n");
        await WriteTextAsync(archive, "statements.tex", main.ToString(), token);
        await WriteTextAsync(archive, "warnings.txt", string.Join('\n', warnings), token);
        await WriteTextAsync(archive, "build.sh", "#!/bin/sh\nset -eu\ncd -- \"$(dirname -- \"$0\")\"\npdflatex -no-shell-escape -halt-on-error -interaction=nonstopmode statements.tex\npdflatex -no-shell-escape -halt-on-error -interaction=nonstopmode statements.tex\n", token);
        foreach (string name in new[] { "olymp.sty", "LICENSE" })
        {
            await using var source = assembly.GetManifestResourceStream("Olymp.Statements.Latex." + name)
                ?? throw new InvalidOperationException("Missing template resource: " + name);
            await using var target = archive.CreateEntry(name).Open();
            await source.CopyToAsync(target, token);
        }
    }

    private static Task WriteTextAsync(ZipArchive archive, string path, string value, CancellationToken token) =>
        WriteBytesAsync(archive, path, Encoding.UTF8.GetBytes(value), token);

    private static async Task WriteBytesAsync(ZipArchive archive, string path, byte[] value, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(path).Open();
        await stream.WriteAsync(value, token);
    }
}
