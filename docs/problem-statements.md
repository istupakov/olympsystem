# Markdown problem statements

Problem texts use Markdown regardless of the contest date.

## Authoring a problem

Place one UTF-8 file named `statement.md` beside `problem.json`. Each document contains one language, including its title. Russian is supported.

```markdown
# Двоичная строка

Дана строка $s$ длины $n$, состоящая из символов `0` и `1`.
Определите, встречается ли в ней хотя бы одна единица.

## Формат входных данных

В единственной строке записана строка $s$, где $1 \le n \le 10^5$.

## Формат выходных данных

Выведите `YES`, если в строке есть хотя бы одна единица, и `NO` иначе.
```

Use one plain-text level-one heading for the title, followed by the description and the two level-two headings shown above, in order. An optional final section begins with `## Примечание`. The title contains only the name. The contest supplies the number, time and memory limits and public test examples. The statement title takes precedence over the JSON name.

## Text and mathematics

Use paragraphs, ordered and unordered lists, emphasis, strong emphasis, inline literals and fenced blocks of literal text. Separate paragraphs with a blank line. Use ordinary Russian punctuation in prose. Images, author-defined tables and embedded HTML are not supported.

Enclose variables and whole mathematical expressions in dollar signs, including single variables in running text. Use `$a_{i+1}$` for subscripts, `$10^{18}$` for powers, `$n \times m$` for dimensions, `$\min(a,b)$` for named functions and `$50\,000$` for a grouped number. For display mathematics put `$$` on separate lines around the expression. The supported notation includes comparisons, fractions, roots, sums, delimiters and cases. Common Unicode signs such as ≤, ≥, ≠, × and … have their mathematical equivalents inside formulas. Unsupported constructs produce source-line diagnostics during import.

## Literal values in Russian prose

Enclose the exact value in Markdown backticks. The renderer supplies the surrounding quotation marks in the prose font and sets the value in monospace. Quote selection uses the number of visible characters (Unicode grapheme clusters, including spaces).

| Markdown source | Appearance in the statement | LaTeX with olymp.sty |
| --- | --- | --- |
| `` `0` `` | ‘`0`’ | `` `\t{0}' `` |
| `` `#` `` | ‘`#`’ | `` `\t{\#}' `` |
| `` `YES` `` | «`YES`» | `<<\t{YES}>>` |
| `` `-1 3` `` | «`-1 3`» | `<<\t{-1 3}>>` |

A single character takes single quotation marks. Two or more take guillemets. Preserve the characters inside the literal, including any quote that belongs to the value. Use `$-1$` for a mathematical quantity and backticks for an exact output token. Fenced blocks and public test examples preserve literal lines without added quotation marks.

## Printable statements

See [TeX archive format and compilation](contest-export.md#printable-statements).
