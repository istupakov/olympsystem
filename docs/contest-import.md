# Contest problem ZIP format

On the contest edit page, upload a ZIP with one folder per problem directly at the root:

```text
A/
  problem.json
  input01.txt
  output01.txt
  input02.txt
  output02.txt
B/
  problem.json
  input1.txt
  output1.txt
```

Folder names are arbitrary. Each folder must contain `problem.json` and at least one
`inputN.txt` / `outputN.txt` pair. Test numbers range from 1 to 999; leading zeros are
allowed. Nested folders and additional files are rejected. JSON uses UTF-8; test files
are preserved as bytes.

Example `problem.json`:

```json
{
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

| Field | Meaning | Default |
|---|---|---|
| `number` | Unique positive problem number within the archive | Required |
| `name` | Problem name | Required |
| `timeLimit` | Time limit in seconds | `1` |
| `slowTimeLimit` | Time limit for slower languages, in seconds | `timeLimit` |
| `memoryLimit` | Memory limit in MiB | `256` |
| `checkerId` | Checker ID; must exist in the database and be supported by the site | `4` |
| `isActive` | Whether the problem is active | `true` |
| `openTestCount` | Number of example tests, ordered by test number | `1` |
| `scores` | Nonnegative scores keyed by test number; omitted tests receive zero | Optional |

Without `scores`, school contests distribute 100 points across non-example tests;
at least one such test is required. Other contest modes leave scores unset.

Import updates problems by number and replaces their tests. Problem IDs, submissions,
checking history and existing statement text are preserved. Problems absent from the
archive are kept. Any error cancels the entire import. After changing tests, start a
retest and regenerate the downloadable tests ZIP as needed.
