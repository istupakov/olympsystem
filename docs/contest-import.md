# Import problems into a contest

Import problems from a ZIP into an existing contest. One ZIP can contain new and existing problems. Each problem is saved separately after confirmation. Import requires permission to manage the contest.

## Prepare the ZIP

Put one directory per problem at the root of the ZIP. A single-contest [export](contest-export.md) is also accepted with its `abbr` directory and `contest.json`. If present, the contest ID in `contest.json` must match the selected contest.

```text
problem01/
  problem.json
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

Only problem parameters, tests, `validator.cpp`, and files under `solutions/` are used. Other exported files, including statements, can remain in the ZIP. Write `problem.json` in UTF-8.

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
- `number` is a nonnegative integer. Duplicate numbers produce a warning without blocking import; export requires unique numbers. `name` and `number` are required for new problems. For updates, omitted problem parameters keep their current values.
- `timeLimit` and `slowTimeLimit` are in seconds. `memoryLimit` is in MiB. `checkerId` must refer to a supported checker. New problems default to 1 second, the regular time limit for `slowTimeLimit`, 256 MiB, checker 4, and `isActive: true`.

## Import tests

Import replaces the problem's complete test set with the files from `tests/`. Without test files, the set becomes empty. Only inactive problems can have an empty test set.

Each test has an `inputN.txt` and an `outputN.txt` with the same test number. Use numbers from 1 through 999. Leading zeros and gaps are allowed. Reject duplicate test numbers, missing partners, and test metadata that refers to absent tests.

Input and output line endings are normalized before validation, solution runs, and saving. Choose CRLF (the default) or LF for the whole import. Other bytes are preserved; no final newline is added. Changing the choice discards previous validation and solution results. The validator retains its platform's line-ending requirements.

- `openTestCount` specifies how many tests are open, starting from the lowest test number. It must be between zero and the size of the replacement set. The default is 1 for a nonempty set and 0 for an empty set.
- `scores` maps test numbers to nonnegative scores. An omitted score is unset. Use an explicit `0` for a zero-point test.
- `inactiveTests` lists disabled test numbers. Other tests are active.
- `testComments` maps test numbers to comments.

`openTests` is not supported. Use `openTestCount` with open tests first in test-number order.

## Validation and saving

- If supplied, `validator.cpp` checks test inputs using a selected C++ compiler; `testlib.h` is available. Failure on an active test blocks saving; failure on an inactive test is a warning. A missing validator, compilation failure, or unavailable Runner is a warning.
- Selected files from `solutions/` run on active tests using the resulting checker and time limits, on available configurations for their language. Results are informational.
- All saved reference solutions and files selected for saving as references must run on the replacement tests before saving. Verdicts need not be accepted, but each solution must produce a result. Results and checking history are saved with the tests. A saved validator is used when the ZIP contains none.
- The regular and slow time limits can be changed independently before saving.

An existing problem with identical resulting parameters and tests, and no selected new references or validator to save, requires no save. Repeating an unchanged import does not replace tests or add checking history.

An updated problem keeps its ID, statement, submissions, and checking history. Problems absent from the ZIP are kept. Finishing import discards unsaved changes and retains saved problems.

Rejudging and publishing a replacement test ZIP are separate operations.
