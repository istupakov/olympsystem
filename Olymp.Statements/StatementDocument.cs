using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Olymp.Statements;

public sealed record StatementDiagnostic(int Line, string Code);

public sealed partial class StatementDocument
{
    public const string InputHeading = "Формат входных данных";
    public const string OutputHeading = "Формат выходных данных";
    public const string NoteHeading = "Примечание";
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseMathematics().UsePipeTables().UsePreciseSourceLocation().Build();
    private readonly MarkdownDocument _document;
    private readonly List<StatementDiagnostic> _diagnostics = [];
    private HeadingBlock? _title;
    private HeadingBlock? _note;

    private StatementDocument(string source)
    {
        Source = source;
        _document = Markdown.Parse(source, Pipeline);
        Validate();
    }

    public string Source { get; }
    public string? Title { get; private set; }
    public IReadOnlyList<StatementDiagnostic> Diagnostics => _diagnostics;
    public bool IsValid => _diagnostics.Count == 0;
    public static StatementDocument Parse(string source) => new(source);

    private void Error(MarkdownObject node, string code) => _diagnostics.Add(new(node.Line + 1, code));

    private void Validate()
    {
        if (_document.FirstOrDefault() is HeadingBlock { Level: 1, Inline.FirstChild: LiteralInline literal } title
            && literal.NextSibling is null && !string.IsNullOrWhiteSpace(literal.Content.ToString()))
        {
            _title = title;
            Title = literal.Content.ToString().Trim();
        }
        else _diagnostics.Add(new(1, "Title"));
        int section = 0;
        foreach (var block in _document)
        {
            if (block == _title) continue;
            if (block is HeadingBlock heading)
            {
                string text = heading.Inline?.FirstChild is LiteralInline value && value.NextSibling is null ? value.Content.ToString() : "";
                string? expected = section switch { 0 => InputHeading, 1 => OutputHeading, 2 => NoteHeading, _ => null };
                if (heading.Level != 2 || text != expected) Error(heading, "Section");
                else
                {
                    section++;
                    if (section == 3) _note = heading;
                }
            }
            ValidateBlock(block);
        }
        if (section < 2) _diagnostics.Add(new(1, "Sections"));
    }

    private void ValidateBlock(Block block)
    {
        switch (block)
        {
            case MathBlock math:
                if (math.ClosingFencedCharCount == 0 || !StatementMath.IsSupported(math.Lines.ToString())) Error(math, "Math");
                break;
            case HeadingBlock:
            case ParagraphBlock:
                break;
            case FencedCodeBlock code:
                if (code.ClosingFencedCharCount == 0) Error(code, "Construct");
                return;
            case ListBlock:
            case ListItemBlock:
                break;
            default:
                Error(block, "Construct");
                return;
        }
        if (block is LeafBlock { Inline: { } inline }) ValidateInline(inline);
        if (block is ContainerBlock container)
            foreach (var child in container)
            {
                if (child is HeadingBlock) Error(child, "Section");
                ValidateBlock(child);
            }
    }

    private void ValidateInline(ContainerInline container)
    {
        foreach (var inline in container)
            switch (inline)
            {
                case MathInline math:
                    if (!StatementMath.IsSupported(math.Content.ToString())) Error(math, "Math");
                    break;
                case EmphasisInline emphasis when emphasis.DelimiterCount is 1 or 2:
                    ValidateInline(emphasis);
                    break;
                case LiteralInline literal:
                    if (literal.Content.ToString().Contains('$')) Error(literal, "Math");
                    break;
                case CodeInline:
                case LineBreakInline:
                    break;
                default:
                    Error(inline, "Construct");
                    break;
            }
    }

    public string Html(bool notesOnly = false) => Render(false, notesOnly);
    public string Latex(bool notesOnly = false) => Render(true, notesOnly);

    private string Render(bool latex, bool notesOnly)
    {
        if (!IsValid) throw new InvalidOperationException("Invalid statement.");
        var result = new StringBuilder();
        bool inNotes = false;
        foreach (var block in _document)
        {
            if (block == _title) continue;
            if (block == _note) inNotes = true;
            if (notesOnly != inNotes) continue;
            RenderBlock(result, block, latex);
        }
        return result.ToString();
    }

    private static void RenderBlock(StringBuilder result, Block block, bool latex, int orderedDepth = 0)
    {
        switch (block)
        {
            case MathBlock math:
                RenderMath(result, math.Lines.ToString(), true, latex);
                break;
            case HeadingBlock heading:
                string text = ((LiteralInline)heading.Inline!.FirstChild!).Content.ToString();
                result.Append(latex ? text switch { InputHeading => "\\InputFile\n", OutputHeading => "\\OutputFile\n", _ => "\\Note\n" }
                    : "<h3 class=\"h5\">" + Escape(text, false) + "</h3>");
                break;
            case ParagraphBlock paragraph:
                if (!latex) result.Append("<p>");
                RenderInline(result, paragraph.Inline!, latex);
                result.Append(latex ? "\n\n" : "</p>");
                break;
            case FencedCodeBlock code:
                string literal = code.Lines.ToString();
                if (latex)
                {
                    result.Append("\\begin{flushleft}\\ttfamily\n");
                    foreach (string line in literal.Split('\n'))
                        result.Append("\\mbox{").Append(EscapeCode(line.TrimEnd('\r'))).Append("}\\\\\n");
                    result.Append("\\end{flushleft}\n");
                }
                else result.Append("<pre><code>").Append(Escape(literal, false)).Append("</code></pre>");
                break;
            case ListBlock list:
                string tag = list.IsOrdered ? "ol" : "ul", environment = list.IsOrdered ? "enumerate" : "itemize";
                result.Append(latex ? $"\\begin{{{environment}}}\n" : list.IsOrdered ? $"<ol start=\"{list.OrderedStart}\">" : "<ul>");
                if (latex && list.IsOrdered && int.TryParse(list.OrderedStart, out int start))
                    result.Append("\\setcounter{enum").Append('i', orderedDepth + 1).Append("}{").Append(start - 1).Append("}\n");
                foreach (var child in list) RenderBlock(result, child, latex, orderedDepth + (list.IsOrdered ? 1 : 0));
                result.Append(latex ? $"\\end{{{environment}}}\n" : $"</{tag}>");
                break;
            case ListItemBlock item:
                result.Append(latex ? "\\item " : "<li>");
                foreach (var child in item) RenderBlock(result, child, latex, orderedDepth);
                if (!latex) result.Append("</li>");
                break;
        }
    }

    private static void RenderInline(StringBuilder result, ContainerInline container, bool latex)
    {
        foreach (var inline in container)
            switch (inline)
            {
                case LiteralInline literal:
                    result.Append(Escape(literal.Content.ToString(), latex));
                    break;
                case CodeInline code:
                    bool single = StringInfo.ParseCombiningCharacters(code.Content).Length == 1;
                    result.Append(latex ? single ? "`\\t{" : "<<\\t{" : single ? "‘<code>" : "«<code>")
                        .Append(latex ? EscapeCode(code.Content) : Escape(code.Content, false))
                        .Append(latex ? single ? "}'" : "}>>" : single ? "</code>’" : "</code>»");
                    break;
                case MathInline math:
                    RenderMath(result, math.Content.ToString(), false, latex);
                    break;
                case EmphasisInline emphasis:
                    string tag = emphasis.DelimiterCount == 2 ? "strong" : "em";
                    result.Append(latex ? emphasis.DelimiterCount == 2 ? "\\textbf{" : "\\emph{" : $"<{tag}>");
                    RenderInline(result, emphasis, latex);
                    result.Append(latex ? "}" : $"</{tag}>");
                    break;
                case LineBreakInline line:
                    result.Append(line.IsHard ? latex ? "\\\\\n" : "<br>" : "\n");
                    break;
            }
    }

    private static void RenderMath(StringBuilder result, string expression, bool display, bool latex)
    {
        string normalized = StatementMath.Normalize(expression);
        if (latex) result.Append(display ? "\\[\n" : "$").Append(normalized).Append(display ? "\n\\]\n" : "$");
        else result.Append(display ? "<div class=\"statement-math\" data-display=\"true\">" : "<span class=\"statement-math\">")
            .Append(WebUtility.HtmlEncode(normalized)).Append(display ? "</div>" : "</span>");
    }

    private static string EscapeCode(string value)
    {
        var expanded = new StringBuilder();
        int column = 0;
        foreach (char character in value)
        {
            if (character == '\t')
            {
                int spaces = 4 - (column % 4);
                expanded.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                expanded.Append(character);
                column++;
            }
        }
        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(expanded.ToString().Normalize(NormalizationForm.FormC));
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            string escaped = Escape(element, true);
            result.Append(element switch
            {
                "-" or "`" or "'" => "{" + escaped + "}",
                " " => "\\ ",
                _ => escaped
            });
        }
        return result.ToString();
    }

    public static string Escape(string value, bool latex)
    {
        if (!latex) return WebUtility.HtmlEncode(value);
        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(value.Normalize(NormalizationForm.FormC));
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (element.Length == 2 && element[1] == '\u0301')
            {
                result.Append("\\'{").Append(element[0]).Append('}');
                continue;
            }
            foreach (char character in element)
                result.Append(character switch
                {
                    '\\' => "\\textbackslash{}",
                    '{' => "\\{",
                    '}' => "\\}",
                    '$' => "\\$",
                    '&' => "\\&",
                    '#' => "\\#",
                    '%' => "\\%",
                    '_' => "\\_",
                    '~' => "\\textasciitilde{}",
                    '^' => "\\textasciicircum{}",
                    '<' => "\\textless{}",
                    '>' => "\\textgreater{}",
                    _ => character.ToString()
                });
        }
        return result.ToString();
    }
}

public static partial class StatementMath
{
    private static readonly HashSet<string> Commands = new(("frac dfrac tfrac sqrt sum prod min max gcd log ln exp sin cos tan "
        + "alpha beta gamma delta epsilon varepsilon zeta eta theta vartheta iota kappa lambda mu nu xi pi varpi rho varrho sigma varsigma tau upsilon phi varphi chi psi omega "
        + "Gamma Delta Theta Lambda Xi Pi Sigma Upsilon Phi Psi Omega "
        + "le leq leqslant ge geq geqslant ne neq lt gt approx sim equiv in notin subset subseteq supset supseteq cup cap emptyset varnothing forall exists "
        + "times cdot div pm mp dots ldots cdots vdots ddots infty to rightarrow leftarrow leftrightarrow Rightarrow Leftarrow Leftrightarrow "
        + "left right big Big bigg Bigg lvert rvert lVert rVert langle rangle lceil rceil lfloor rfloor "
        + "limits nolimits overline underline binom dbinom tbinom mathrm mathbf mathit mathbb mathcal text quad qquad begin end").Split(' '), StringComparer.Ordinal);

    [GeneratedRegex(@"\\([a-zA-Z]+|.)", RegexOptions.Singleline)]
    private static partial Regex CommandPattern { get; }

    [GeneratedRegex(@"\\(begin|end)\{([^{}]*)\}")]
    private static partial Regex EnvironmentPattern { get; }

    [GeneratedRegex(@"\\text\{([^{}]*)\}")]
    private static partial Regex TextPattern { get; }

    private static readonly string[] DelimiterCommands = [@"\{", @"\}", @"\langle", @"\rangle", @"\lceil", @"\rceil", @"\lfloor", @"\rfloor", @"\lvert", @"\rvert", @"\lVert", @"\rVert", @"\|"];

    public static string Normalize(string value) => CommandPattern.Replace(value.Replace("≤", @"\le ", StringComparison.Ordinal)
        .Replace("≥", @"\ge ", StringComparison.Ordinal).Replace("≠", @"\ne ", StringComparison.Ordinal)
        .Replace("×", @"\times ", StringComparison.Ordinal).Replace("…", @"\ldots ", StringComparison.Ordinal)
        .Replace("−", "-", StringComparison.Ordinal), match => match.Groups[1].Value switch
        {
            "lt" => "<",
            "gt" => ">",
            _ => match.Value
        });

    public static bool IsSupported(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16000) return false;
        value = Normalize(value);
        foreach (Match match in CommandPattern.Matches(value))
        {
            string command = match.Groups[1].Value;
            if (!Commands.Contains(command) && !(command.Length == 1 && ",;:! {}_%#$&|\\".Contains(command, StringComparison.Ordinal))) return false;
        }
        int delimiters = 0;
        foreach (Match match in CommandPattern.Matches(value))
        {
            if (match.Groups[1].Value is not ("left" or "right")) continue;
            if (match.Groups[1].Value == "left") delimiters++;
            else if (--delimiters < 0) return false;
            string next = value[(match.Index + match.Length)..].TrimStart();
            if (next.Length == 0 || (!".()[]|/".Contains(next[0]) && !DelimiterCommands.Any(next.StartsWith))) return false;
        }
        if (delimiters != 0) return false;
        var textGroups = TextPattern.Matches(value);
        if (CommandPattern.Matches(value).Count(m => m.Groups[1].Value == "text") != textGroups.Count
            || textGroups.Any(m => m.Groups[1].Value.Any(c => "_$&^#%\\".Contains(c)))) return false;
        var environments = new Stack<string>();
        int environmentCount = 0;
        foreach (Match match in EnvironmentPattern.Matches(value))
        {
            environmentCount++;
            string environment = match.Groups[2].Value;
            if (environment is not ("cases" or "aligned")) return false;
            if (match.Groups[1].Value == "begin") environments.Push(environment);
            else if (!environments.TryPop(out string? opening) || opening != environment) return false;
        }
        if (environments.Count != 0) return false;
        if (CommandPattern.Matches(value).Count(m => m.Groups[1].Value is "begin" or "end") != environmentCount) return false;
        if (environmentCount == 0 && value.Contains('&')) return false;
        string remaining = CommandPattern.Replace(value, "");
        int braces = 0;
        foreach (char character in remaining)
        {
            if (character == '{' && ++braces > 32) return false;
            if (character == '}' && --braces < 0) return false;
            if (character is '%' or '#' or '$' or '\\' || (char.IsControl(character) && !char.IsWhiteSpace(character))) return false;
        }
        int position = 0;
        return braces == 0 && ReadSequence(value, ref position, '\0', 0);
    }

    private static bool ReadSequence(string value, ref int position, char closing, int depth)
    {
        if (depth > 32) return false;
        while (position < value.Length)
        {
            if (value[position] == closing) { position++; return true; }
            if (char.IsWhiteSpace(value[position])) { position++; continue; }
            if (value[position] is '^' or '_')
            {
                position++;
                if (!ReadAtom(value, ref position, depth + 1)) return false;
            }
            else if (!ReadAtom(value, ref position, depth + 1)) return false;
        }
        return closing == '\0';
    }

    private static bool ReadAtom(string value, ref int position, int depth)
    {
        if (depth > 32) return false;
        while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
        if (position == value.Length || value[position] is '}' or '^' or '_') return false;
        char next = value[position++];
        if (next == '{') return ReadSequence(value, ref position, '}', depth + 1);
        if (next != '\\') return true;
        int start = position;
        while (position < value.Length && char.IsAsciiLetter(value[position])) position++;
        if (start == position) { position++; return position <= value.Length; }
        string command = value[start..position];
        if (command is "sqrt")
        {
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
            if (position < value.Length && value[position] == '[')
            {
                position++;
                if (!ReadSequence(value, ref position, ']', depth + 1)) return false;
            }
        }
        int arguments = command switch
        {
            "frac" or "dfrac" or "tfrac" or "binom" or "dbinom" or "tbinom" => 2,
            "sqrt" or "overline" or "underline" or "mathrm" or "mathbf" or "mathit" or "mathbb" or "mathcal" or "text" or "begin" or "end" => 1,
            _ => 0
        };
        for (int i = 0; i < arguments; i++)
            if (!ReadAtom(value, ref position, depth + 1)) return false;
        return true;
    }
}
