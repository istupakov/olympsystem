# Import problems into a contest

Import problems from a ZIP into an existing contest. One ZIP can contain new and existing problems. Each problem is saved in a separate transaction. See [import permissions](access-control.md#10-jury-administration-and-service-accounts).

Include an optional UTF-8 `statement.md` beside `problem.json`, using the [Markdown statement format](problem-statements.md), regardless of the contest date.

## Prepare the ZIP

Put one directory per problem at the root of the ZIP. A single-contest [export](contest-export.md) is also accepted with its `abbr` directory and `contest.json`. If present, the contest ID in `contest.json` must match the selected contest.

```text
problem01/
  problem.json
  statement.md
  validator.cpp
  tests/
    input001.txt
    output001.txt
    input002.txt
    output002.txt
  solutions/
    reference.cpp
    reference.py
```

Problem parameters, Markdown statements, tests, `validator.cpp`, and files under `solutions/` are used. Other exported files can remain in the ZIP. Write `problem.json` in UTF-8.

```json
{
  "id": 417,
  "number": 1,
  "name": "Sum",
  "timeLimit": 1,
  "slowTimeLimit": 2,
  "memoryLimit": 256,
  "checkerId": 4,
  "isActive": true,
  "openTestCount": 1,
  "scores": { "1": 0, "2": 100 }
}
```

- Include `id` to update that problem in the selected contest. An unknown ID or an ID from another contest is an error. Omit `id` to create a problem and let the database assign its ID.
- `number` is a nonnegative integer. Duplicate numbers produce a warning without blocking import; full and simplified exports require unique numbers. New problems require a number and an effective name. A valid Markdown title supplies the name and takes precedence over JSON, including on updates that retain the saved Markdown. Supplied JSON names are compared after trimming outer whitespace, with case and punctuation significant; mismatches are warnings. Omitted problem parameters keep their current values.
- `timeLimit` and `slowTimeLimit` are in seconds. `memoryLimit` is in MiB. `checkerId` must refer to a supported checker. New problems default to 1 second, the regular time limit for `slowTimeLimit`, 256 MiB, checker 4, and `isActive: true`.

## Import tests

A nonempty `tests/` replaces the complete test set. A missing or empty directory preserves stored test IDs, bytes, flags, scores and comments; archive test options and line-ending normalization do not alter retained tests. New problems without tests are created inactive. Only inactive problems can have an empty test set.

Each test has an `inputN.txt` and an `outputN.txt` with the same test number. Use numbers from 1 through 999. Leading zeros and gaps are allowed. Reject duplicate test numbers, missing partners, and test metadata that refers to absent tests.

Supplied input and output line endings are normalized before validation, solution runs, and saving. Line endings use CRLF by default; LF can be specified for the whole import. Other bytes are preserved; no final newline is added. Changing the choice discards validation and solution results for supplied tests. The validator retains its platform's line-ending requirements.

- `openTestCount` specifies how many tests are open, starting from the lowest test number. It must be between zero and the size of the replacement set. The default is 1 for a nonempty set and 0 for an empty set.
- `scores` maps test numbers to nonnegative scores. An omitted score is unset. Use an explicit `0` for a zero-point test.
- `inactiveTests` lists disabled test numbers. Other tests are active.
- `testComments` maps test numbers to comments.

`openTests` is not supported. Use `openTestCount` with open tests first in test-number order.

## Validation and saving

Validators and solutions run only on explicit request. Preparing an import, changing its settings and saving do not start runs. New or changed archive solutions are selected for saving by default and can be excluded individually.

- Compilers for solutions and validators must be active database records. Each run requires an available Runner matching the compiler; unavailable compilers produce an error.
- If supplied, `validator.cpp` checks test inputs using a selected C++ compiler; `testlib.h` is available. Failure on an active test or an unavailable Runner blocks saving; failure on an inactive test is a warning. A missing validator or compilation failure is a warning.
- Selected files from `solutions/` run on active tests using the resulting checker, time limits, and selected compiler. Results are informational.
- Retained reference solutions require current results when tests or judging parameters change; new references require current results on the effective active tests. Verdicts need not be accepted, but each solution must produce a result. Matching filenames replace a reference when its code or compiler differs; the previous submission and history are retained. A supplied validator checks the effective tests; a saved validator is checked when tests change.
- Within one import, completed results are reused while their inputs remain unchanged. Changing a time limit invalidates runs using that limit; changing line-ending normalization invalidates results for supplied tests. Failed Runner requests are retried; a forced run bypasses reuse.
- For an existing problem, the validator can be saved separately after compilation and a complete check against the stored test inputs. Rejected tests require confirmation; archive tests, parameters, and other solutions are not saved. Replacing a validator retains the previous submission and history. Normal problem import still rejects validation failures on active tests.

An omitted statement, validator or solution set preserves stored data. An empty or invalid supplied Markdown statement is an error for that problem, with source-line diagnostics. Missing statements are allowed in drafts. Statement-only updates retain tests, submissions, references and checking history.

An existing problem with identical resulting parameters, statement and tests, and no selected new references or validator to save, is a no-op: it does not replace tests or add checking history.

An updated problem keeps its ID and submissions. Concurrent changes to the stored problem invalidate the prepared import. Problems absent from the ZIP are kept.

Rejudging and publishing a replacement test ZIP are separate operations.
