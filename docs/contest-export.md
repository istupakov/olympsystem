# OlympSystem contest export

This document specifies version 1 of the full contest export format and the simplified problem archive. The exported files can be tracked in Git and used to prepare import packages.

## Archive layout

The full ZIP contains one top-level directory per contest, named after its `abbr`. Use the same layout for single-contest and all-contest exports. Place problem directories directly inside the contest directory. Use `problem` followed by the decimal problem number padded to at least two digits, such as `problem01/` and `problem02/`.

Problem numbers may be zero: export problem 0 as `problem00/` and preserve `"number": 0` in its metadata. Negative problem numbers and duplicate numbers within a contest are rejected. Test numbers must remain positive.

```text
acm2019/
  contest.json
  description.html
  statements.pdf
  published-tests.zip
  tables/
    final.html
    official.html
  participants.jsonl
  messages.jsonl
  results.jsonl
  problem01/
    problem.json
    statement.md
    tests/
      input001.txt
      output001.txt
    jury-solutions/
      solutions.jsonl
      <sha256>.src
```

`contest.json` and `problem.json` retain their source database IDs for matching records on import. Changing `abbr` or the problem number changes the corresponding directory on the next export. Changing a display name leaves the paths unchanged. Report invalid or duplicate archive paths with the relevant contest or problem IDs.

Write JSON and JSONL as UTF-8 without a BOM, using LF line endings. Preserve Cyrillic and symbols such as `+` as literal characters (`C++`, not `C\u002B\u002B`); retain required JSON escaping for quotation marks, backslashes, and control characters. Indent JSON. Write one object per line in JSONL. Sort records by ID unless a different order is specified. Keep field and key order stable and omit the export timestamp. Stability applies to the extracted files.

`contest.json`, each `problem.json`, and the three contest-level JSONL files are required. A JSONL file with no records is empty. Include statements, attachments, and jury solutions when present. Export a problem without tests with `openTestCount: 0` and no test files.

## Contest metadata and attachments

`contest.json` contains `formatVersion: 1`, `id`, `name`, `abbr`, `startTime`, `endTime`, `problemShowTime`, `freezeTime`, `isOpen`, and `isHidden`. Write dates in ISO 8601 with their original offsets. Use `null` for absent dates.

The file also contains `checkers` and `compilers`, sorted by ID. Include `id` and `description` for each checker referenced by an exported problem. Include `id`, `language`, and `name` for each compiler referenced by an exported jury attempt.

- `description.html` contains the saved contest description.
- `tables/final.html` and `tables/official.html` contain the saved final and official standings. Use the existing OlympSystem decoding for historical tables and write the text in UTF-8. Export saved tables only.
- `statements.pdf` is a byte-for-byte copy of the saved contest statement PDF.
- `published-tests.zip` is a byte-for-byte copy of the saved published test ZIP. Export the database test pairs separately.

## Problems, statements, and tests

Include all contest problems, including inactive ones. `problem.json` contains `id`, `number`, `name`, `timeLimit`, `slowTimeLimit`, `memoryLimit`, `checkerId`, `isActive`, and `openTestCount`. Use seconds and MiB for limits, as in the [existing problem format](contest-import.md).

Export the complete saved UTF-8 problem text as `statement.md`, without conversion. See the [Markdown statement format](problem-statements.md). Version 1 does not include separate statement assets. Examples are stored in open tests.

- Copy all test inputs and outputs from the database into `tests/`, byte for byte, including inactive tests. Use `inputNNN.txt` and `outputNNN.txt` with at least three digits. Preserve the database test numbers, including gaps.
- `openTestCount` is the number of tests at the start of the sequence ordered by `Test.Number` that have `IsOpen` set. Test activity is recorded separately.
- `openTests` is an optional list of all open test numbers in ascending order. Include it only when an open test follows a closed test in the sequence ordered by `Test.Number`. When present, this list takes precedence over `openTestCount` and defines the complete set of open tests. Keep the original test numbers and `openTestCount` unchanged. For example, if test 1 is closed and only test 2 is open, write `"openTestCount": 0` and `"openTests": [2]`.
- `scores` is an optional object mapping test numbers to stored scores. Include explicit zeroes. A missing key represents a `null` score. Omit the object if empty.
- `inactiveTests` is an optional list of inactive test numbers in ascending order.
- `testComments` is an optional object mapping test numbers to their nonempty comments.

## Participants, messages, and results

Export `Competitor` records for the contest where `IsHidden == false`.

| File | Record fields | Selection |
| --- | --- | --- |
| `participants.jsonl` | `id`, `name`, `isApproved`, `isOutOfCompetition`, `isDisqualified` | All exported participants, including those with no submissions. |
| `messages.jsonl` | `id`, `userId`, `problemId`, `sendTime`, `userText`, `juryText` | Contest messages without an author and messages whose author is in `participants.jsonl`. Include general announcements and problem clarifications. Write absent values as `null`. |
| `results.jsonl` | `submissionId`, `problemId`, `userId`, `commitTime`, `statusCode`, `score` | Submissions by exported participants for contest problems where `Submission.IsHidden == false` and `StartTime <= CommitTime <= EndTime`. |

Preserve the saved `statusCode` and `score`, including a `null` score. Include submissions between the freeze and the end of the contest. The files provide the data to reconstruct standings for registered participants using the [OlympSystem scoring rules](../Olymp.Domain/Standing.cs). Export only the listed fields. Participant source code, diagnostics, IP addresses, and credentials are outside this format.

## Candidate jury solutions

For each problem, export submissions **from hidden accounts** before, during, and after the contest. Select them using `Submission.User.IsHidden`. Both values of `Submission.IsHidden` and `IsReference` are eligible.

Deduplicate identical source bytes within each problem across all compilers. Store one byte-for-byte copy as `jury-solutions/<sha256>.src`, using a lowercase hexadecimal SHA-256 hash. Write one record per source file in `solutions.jsonl`, ordered by file name. Keep every attempt in `attempts`, ordered by `submissionId`. Each attempt includes `authorName`, the display name stored in `Submission.User.Name`.

```jsonl
{"file":"<sha256>.src","attempts":[{"submissionId":18425,"authorName":"Jury member A","compilerId":4,"secondsFromStart":-3600,"statusCode":2,"score":null,"isReference":true},{"submissionId":18512,"authorName":"Jury member B","compilerId":7,"secondsFromStart":120.5,"statusCode":2,"score":null}]}
```

`secondsFromStart` is the signed difference between `CommitTime` and `StartTime` in seconds, with a fractional part where needed. Include `isReference` when the submission is marked as a reference. The verdict and score describe the saved attempt.

## Simplified problem archive

The simplified ZIP contains all problems, including inactive ones, in root-level `problemNN/` directories. Each contains `problem.json` with the original ID, byte-for-byte test files, any saved validator and reference solutions in UTF-8, and `statement.md` when present. Contest metadata, participants, results, and check history are omitted.

The archive follows the [import format](contest-import.md). Export is rejected if test numbers fall outside 1–999 or an open test follows a closed test in test-number order.

## Printable statements

The TeX ZIP contains active problems, original problem numbering, and only active open tests as examples. Missing or invalid statements produce a warning and a placeholder rather than blocking export; `warnings.txt` lists affected problems.

The ZIP includes `statements.tex`, individual problem files, sample files, the bundled `olymp.sty`, its license, and `build.sh`. Compilation requires TeX Live with pdfLaTeX, Russian Babel, T2A fonts, `amsmath`, `amssymb` and `verbatim`. On Ubuntu, install `texlive-latex-base`, `texlive-lang-cyrillic`, `texlive-fonts-recommended` and `cm-super`. Extract the complete archive, then run `sh build.sh` from its directory. This runs pdfLaTeX twice and produces `statements.pdf`. PDF downloads compile the same printable statements on the server.

## Export rules

Export is read-only and follows the [contest material read permissions](access-control.md#10-jury-administration-and-service-accounts). Exporting all contests includes hidden and archived contests.

The archive contains a consistent snapshot of the database. Concurrent changes may wait until archive generation finishes. Invalid or duplicate archive paths cancel the complete export; no partial archive is returned.
