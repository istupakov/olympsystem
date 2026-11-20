using System.IO.Compression;
using System.Text;

using Olymp.Statements;

using Xunit;

namespace Olymp.Tests.Statements;

public class StatementTests
{
    public const string Source = """
        # Двоичная строка

        Символ `0`, ответ `YES`, знак `#`, символ `е́` и $a_{i+1} \le 10^9$.

        - Первый пункт
        - **Второй** пункт

        ## Формат входных данных

        Строка $s$.

        $$
        \frac{a}{b} + \sqrt{n} \ge 0
        $$

        ## Формат выходных данных

        Выведите `-1 3` и `_%&{}\^~$`.

        ## Примечание

        *Пояснение*.
        """;

    [Fact]
    public void RendersRussianLiteralsMathAndSectionsFromOneDocument()
    {
        var document = StatementDocument.Parse(Source);
        Assert.Empty(document.Diagnostics);
        Assert.Equal("Двоичная строка", document.Title);
        string html = document.Html(), tex = document.Latex();
        Assert.Contains("‘<code>0</code>’", html);
        Assert.Contains("«<code>YES</code>»", html);
        Assert.Contains("‘<code>", html);
        Assert.Contains("`\\t{0}'", tex);
        Assert.Contains("<<\\t{YES}>>", tex);
        Assert.Contains("`\\t{\\#}'", tex);
        Assert.Contains("`\\t{\\'{е}}'", tex);
        Assert.Contains("\\frac{a}{b}", tex);
        Assert.Contains("\\InputFile", tex);
        Assert.Contains("\\OutputFile", tex);
        Assert.DoesNotContain("\\Note", tex);
        Assert.StartsWith("\\Note", document.Latex(notesOnly: true));
    }

    [Fact]
    public void CodeKeepsHyphensSpacesBlankLinesAndTabStopsWithoutAllowingTexCommands()
    {
        const string code = "for (;;) {\n\t--i;\n\n  \t\\end{flushleft}%\n}";
        var document = StatementDocument.Parse(Source.Replace("Строка $s$.", "`--i` и `''`.\n\n```\n" + code + "\n```", StringComparison.Ordinal));
        Assert.True(document.IsValid);
        string tex = document.Latex();
        Assert.Contains("<<\\t{{-}{-}i}>>", tex);
        Assert.Contains("<<\\t{{'}{'}}>>", tex);
        Assert.Contains("\\mbox{\\ \\ \\ \\ {-}{-}i;}\\\\\n\\mbox{}\\\\\n", tex);
        Assert.Contains("\\mbox{\\ \\ \\ \\ \\textbackslash{}end\\{flushleft\\}\\%}", tex);
        Assert.Contains("\\'{е}", tex);
        Assert.Contains("\t--i;\n\n", document.Html());
    }

    [Theory]
    [InlineData(@"1 \le n \leqslant 10^9")]
    [InlineData(@"a_1 + a_2 + a_3 = b_1 + b_2")]
    [InlineData(@"\text{a = b} + x > 0")]
    public void MathKeepsExpressionsAndLeavesLineBreakingToPreamble(string expression)
    {
        var document = StatementDocument.Parse(Source.Replace("Строка $s$.", $"${expression}$", StringComparison.Ordinal));
        Assert.True(document.IsValid);
        Assert.Contains("$" + expression + "$", document.Latex());
        Assert.DoesNotContain("\\allowbreak", document.Latex());
        Assert.Contains("\\[\n\\frac{a}{b} + \\sqrt{n} \\ge 0\n\\]", document.Latex());
    }

    [Theory]
    [InlineData("# **Title**\n\n## Формат входных данных\n\n## Формат выходных данных", "Title")]
    [InlineData("# Title\n\n## Формат входных данных\n\n## Формат входных данных", "Section")]
    [InlineData("# Title\n\nText", "Sections")]
    [InlineData("# Title\n\n<script>alert(1)</script>\n\n## Формат входных данных\n\n## Формат выходных данных", "Construct")]
    [InlineData("# Title\n\n![image](image.png)\n\n## Формат входных данных\n\n## Формат выходных данных", "Construct")]
    [InlineData("# Title\n\n$\\input{secret}$\n\n## Формат входных данных\n\n## Формат выходных данных", "Math")]
    [InlineData("# Title\n\n$n + 1\n\n## Формат входных данных\n\n## Формат выходных данных", "Math")]
    [InlineData("# Title\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n## Формат входных данных\n\n## Формат выходных данных", "Construct")]
    public void RejectsInvalidStatementsWithSourceLocations(string source, string code)
    {
        var document = StatementDocument.Parse(source);
        Assert.False(document.IsValid);
        Assert.Contains(document.Diagnostics, d => d.Code == code && d.Line > 0);
    }

    [Theory]
    [InlineData("2026-10-31T16:59:59Z", false)]
    [InlineData("2026-10-31T17:00:00Z", true)]
    [InlineData("2026-11-01T00:00:00+07:00", true)]
    [InlineData("2026-10-31T19:00:00+02:00", true)]
    public void CutoffComparesInstantsInclusively(string timestamp, bool expected) =>
        Assert.Equal(expected, StatementFormat.UsesMarkdown(DateTimeOffset.Parse(timestamp)));

    [Theory]
    [InlineData(@"\begin{cases} a & x \le 0 \\ b & x > 0 \end{cases}", true)]
    [InlineData(@"\begin{aligned} x &= 1 \\ y &= 2 \end{aligned}", true)]
    [InlineData(@"\href{file}{x}", false)]
    [InlineData(@"\def\foo{x}", false)]
    [InlineData(@"\begin{document}x\end{document}", false)]
    [InlineData(@"\begin{cases}x\end{aligned}", false)]
    [InlineData(@"\frac{a}{b", false)]
    [InlineData(@"\frac{a}", false)]
    [InlineData(@"n^", false)]
    [InlineData(@"\left(x", false)]
    [InlineData(@"\left x \right y", false)]
    [InlineData(@"\left(x\right)", true)]
    [InlineData(@"\text{если } n \gt 0", true)]
    [InlineData(@"\text{a_b}", false)]
    [InlineData(@"\begin cases", false)]
    public void ChecksSharedMathSubset(string formula, bool expected) => Assert.Equal(expected, StatementMath.IsSupported(formula));

    [Fact]
    public async Task TexArchiveHasTemplateExamplesOriginalNumbersAndWarningPlaceholders()
    {
        using var stream = new MemoryStream();
        await StatementTexArchive.WriteAsync(stream, "Турнир #1", StatementFormat.Cutoff,
            [new(2, "Задача", 1, 256, StatementDocument.Parse(Source), [new("a\nb\n"u8.ToArray(), "YES\n"u8.ToArray())]),
                new(7, "Без условия", 2, 512, null, [])], "Условие отсутствует", TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        Assert.NotNull(zip.GetEntry("olymp.sty"));
        Assert.NotNull(zip.GetEntry("LICENSE"));
        string first = await ReadAsync(zip, "problem00.tex"), second = await ReadAsync(zip, "problem01.tex");
        Assert.Contains("\\setcounter{problem}{1}", first);
        Assert.Contains("\\setcounter{problem}{6}", second);
        Assert.Contains("\\Example\n\\begin{example}%\n\\exmpfile{samples/problem00-1.in}{samples/problem00-1.out}%\n\\end{example}", first);
        Assert.Equal("a\nb\n", await ReadAsync(zip, "samples/problem00-1.in"));
        Assert.Contains("Условие отсутствует", second);
        Assert.Contains("7. Без условия", await ReadAsync(zip, "warnings.txt"));
        string main = await ReadAsync(zip, "statements.tex");
        Assert.Contains("\\binoppenalty=10000\n\\relpenalty=10000\n\\exhyphenpenalty=10000", main);
        Assert.DoesNotContain("\\renewcommand{\\contestname}", main);
        Assert.DoesNotContain("StatementSetupContest", main);
        Assert.Null(zip.GetEntry("preamble.tex"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ExampleHeadingAndRowsMatchSampleCountAndKeepOriginalBytes(int count)
    {
        using var stream = new MemoryStream();
        var examples = Enumerable.Range(1, count).Select(index => new StatementExample(
            Encoding.UTF8.GetBytes($"  {index}\t\n\n"), Encoding.UTF8.GetBytes($"{index}\n"))).ToArray();
        await StatementTexArchive.WriteAsync(stream, "Contest", StatementFormat.Cutoff,
            [new(1, "Problem", 1, 256, StatementDocument.Parse(Source), examples)], "Missing", TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        string tex = await ReadAsync(zip, "problem00.tex");
        Assert.Equal(count * 2, zip.Entries.Count(entry => entry.FullName.StartsWith("samples/", StringComparison.Ordinal)));
        if (count == 0) Assert.DoesNotContain("\\Example", tex);
        else Assert.Contains(count == 1 ? "\\Example\n" : "\\Examples\n", tex);
        for (int index = 1; index <= count; index++)
        {
            Assert.Contains($"\\exmpfile{{samples/problem00-{index}.in}}{{samples/problem00-{index}.out}}%\n", tex);
            Assert.Equal($"  {index}\t\n\n", await ReadAsync(zip, $"samples/problem00-{index}.in"));
            Assert.Equal($"{index}\n", await ReadAsync(zip, $"samples/problem00-{index}.out"));
        }
    }

    [Fact]
    public async Task HeaderUsesStandardContestCommandWithEscapedNameAndStartDate()
    {
        using var stream = new MemoryStream();
        var start = new DateTimeOffset(2026, 11, 21, 1, 0, 0, TimeSpan.FromHours(7));
        await StatementTexArchive.WriteAsync(stream, "Contest #1 & \\name", start, [], "Missing", TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        Assert.Contains("\\contest\n{Novosibirsk 2026, NSTU}%\n{Contest \\#1 \\& \\textbackslash{}name}%\n{Novosibirsk, November 21, 2026}%", await ReadAsync(zip, "statements.tex"));
    }

    private static async Task<string> ReadAsync(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
