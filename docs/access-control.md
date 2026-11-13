# Access control rules

Agreed access rules for Olymp.WebApp. Changes to these rules require a separate
agreement.

## 1. Actors and account restrictions

| Actor | Meaning and current representation |
| --- | --- |
| Guest | No authenticated account. |
| Permanent user | An `OlympUser` using a personal account. |
| Contest participant | A separate `Competitor` account, individual or team, bound to one contest by `competitor-contest`. |
| Coach | A permanent user. Organization-based registration and early printing require `coach-org` claims; submission access uses the author's stored `User.CoachId` relationship without requiring a coach claim. |
| Jury member | An account with the `Jury` role; `contest-manage` grants management access to a particular contest. |
| Administrator | An account with the `Admin` role. |

Roles can overlap. Coaches and permanent users share the general
interface. Temporary participant accounts may browse other visible contests and
published statements under the same publication rules as other viewers. Their
contest binding restricts participation and submission attribution, not public
read access. They cannot register as permanent users or read other participants'
private submissions.

Shared user-facing pages and their endpoints have no jury or
administrator overrides. Staff accounts follow the same publication, ownership,
submission, download, and freeze rules as other users in this interface. Ordinary
owner/member permissions and coach permissions still apply when the account
independently qualifies for them. Expanded staff access belongs exclusively to
the separate jury/administration interface described in section 10.

Protected application pages require authentication and
completion of the email-as-login transition. Account management remains available
to permanent users who still need to complete that transition. Temporary accounts
are exempt from this transition but are excluded by the `OlympUser` policy.
Passkey endpoints additionally depend on the configured feature flag.

Protected operations use the following account policies:

| Policy | Requirements | Usage |
| --- | --- | --- |
| Default (`[Authorize]`) | Authenticated account and completed email migration; temporary accounts are exempt from migration. | Shared protected pages and source downloads, including temporary participants. |
| `PermanentAccount` | Authenticated account without the temporary-participant claim; migration is not required. | Account management, including email migration itself. |
| `OlympUser` | Default policy plus `PermanentAccount`. | Individual registration and test downloads. |
| `Coach` | `OlympUser` plus the coach claim. | Team registration and the protected PDF printing endpoint. |
| `SignedIn` | Authentication only. | Signed-in navigation, including before email migration. |

Passkey sign-in remains available to guests when the feature is enabled.

Signing in requires account confirmation. For a
temporary participant, confirmation means approval and absence of disqualification.
For a permanent user, the default email-confirmation check applies. Lockout is a
separate sign-in restriction.

`IsHidden` controls visibility; it is not equivalent to account lockout or
`IsDisqualified`. The effect of disqualification must be specified per operation.

Sources: [policy and claim names](../Olymp.WebApp/Names.cs),
[WebApp policies](../Olymp.WebApp/Program.cs),
[email migration](../Olymp.WebApp/Components/Account/EmailAsLoginRequirement.cs),
[account confirmation](../Olymp.WebApp/Services/Identity/OlympUserConfirmation.cs).

## 2. Contest state and publication

Time-based availability and actor authorization are separate checks.

| Property or flag | Meaning in current Domain code |
| --- | --- |
| `IsHidden` | Excludes a contest from ordinary visibility and public material availability. Staff permissions are defined in section 10. |
| `AllowShowProblems` | The contest is not hidden and its start time has been reached. Used by the WebApp workspace and navigation; statement/source queries use the equivalent SQL predicate. |
| `AllowShowStanding` | The contest is not hidden and its start time has been reached. |
| `AllowDownloadProblems` | The contest is not hidden and either its start time or `ProblemShowTime` has been reached. Actor permissions require an additional check. |
| `AllowDownloadTests` | The contest is not hidden, is open, and has ended. |
| `AllowSubmitSolution` | The contest is not hidden, has started, and either has not ended or is open. |
| `AllowSendMessage` | The contest is not hidden and the current time is within its scheduled duration. |
| `IsOpen` | Allows permanent users to submit without contest registration, including after the scheduled end. It does not by itself mean that the contest has ended. |

An open contest cannot have a freeze configured: `IsOpen` requires
`FreezeTime == null`. Opening a contest requires clearing its freeze.
Submission lists ignore freeze restrictions in open contests even if an
inconsistent record retains a non-null `FreezeTime`.

`ProblemShowTime` exists to allow PDF printing before the contest
starts. It does not publish HTML statements early.

The problem workspace requires sign-in and shows only active problems in visible,
started contests (`AllowShowProblems`). Closing submissions does not remove read
access. Temporary accounts can also read other contests' published materials.
Submission ownership and permission to submit are separate decisions.

Source: [Domain availability properties](../Olymp.Domain/DomainObjects.cs),
[problem workspace](../Olymp.WebApp/Components/Pages/Contests/Problems.razor),
[contest downloads](../Olymp.WebApp/Components/Contests/ContestDownloads.cs).

## 3. Public pages and downloadable materials

Guests can read news, contacts, general rules, the visible
contest list, contest rules, public participant lists, and published standings.
Public participant lists omit hidden participants and account credentials.
Standings are published from the contest start. Hidden contests are unavailable
on these routes, including staff accounts.

| Material | Access |
| --- | --- |
| Statement PDF after the start | Anonymous download when the contest is visible and the file exists. |
| Statement PDF before the start | Permanent account passing the `Coach` policy, from `ProblemShowTime`, with a participant in this contest whose `CoachId` matches the account. Visible contest and existing file required; participant approval is not required. |
| HTML statements and examples | Signed-in account and a visible, active problem, including temporary accounts viewing another contest. Examples contain only active, open tests. |
| Test ZIP | Permanent account passing `OlympUser`, linked as a member or coach to a participant in this contest; visible, open contest strictly after its end and an existing file. Participant approval is not required. |

Early printing uses the protected `/contests/{id}/printproblems` endpoint;
the public `/contests/{id}/downloadproblems` endpoint is available only after
the start. From `ProblemShowTime` until the start, navigation offers the printing
link; guests must sign in and unauthorized accounts are denied. After the start,
the PDF is public. A coach claim does not override contest visibility.

Test archives require the member or coach relationship in the requested contest.
Organization membership, a coach claim alone, and staff roles are insufficient.

Sources: [downloads](../Olymp.WebApp/Components/Contests/ContestDownloads.cs),
[participants](../Olymp.WebApp/Components/Pages/Contests/Participants.razor),
[standings page](../Olymp.WebApp/Components/Pages/Contests/Standing.razor).

## 4. Registration and participant credentials

Contest abbreviations starting with `icpc` use the ACM team format, scoring and
standings. Manual team registration in these contests is available only out of
competition; the service rejects official manual registration as well as the UI.
Official teams can be imported from icpc.global in the staff participant interface
by an administrator or a jury member with the stored `contest-manage` permission
for the target contest. The same permission and the `icpc` prefix are rechecked
when saving.

ICPC import requires access to the selected contest in icpc.global. It links only
existing permanent accounts by normalized email, never creates permanent accounts
or grants coach claims, and requires an explicit choice for ambiguous coaches.
Unmatched roster names remain unlinked. Organizations use existing local records.
Only ICPC registration status `ACCEPTED` sets approval; eligibility does not
override it. Reimports update selected teams' names, organization, roster, coach,
and approval while retaining credentials, submissions, history, and other flags.
They do not delete absent teams. Stale or ambiguous matches and duplicate
memberships are rejected. Import is transactional. ICPC credentials, tokens,
sessions, and upstream response bodies are not retained or logged.

- Individual registration requires a permanent account and a visible, individual
  contest before its end. First name, last name, and organization are required.
  Registration in competition additionally requires a group/profile detail.
  A user without that detail can register only out of competition.
- Team registration requires a permanent coach account with a matching
  organization claim and a visible team contest before its end. Linked members
  must belong to that organization, be visible, and not already participate in
  the same contest. Team composition validation is independent of authorization.
- Coaches can list teams from their authorized organizations. Temporary account
  logins appear in that list; passwords are shown only to the team's own coach.
  These credentials do not appear in the public participant list.
- An individual registered out of competition can see their own assigned
  participant login and password on the individual registration page. Official
  participants continue to receive credentials from the organizer. Account
  issuance and approval are separate; registration does not generate logins
  or passwords. Missing credentials are shown as not yet issued.
- An individual permanent user can cancel their own registration before the
  contest starts, while the contest is visible and has not ended.
- A permanent coach with the matching organization claim can cancel their own
  team before the start, while the contest is visible and has not ended, if the
  team is unapproved or participates out of competition. They cannot cancel
  another coach's team. Membership alone does not grant team cancellation.
- Cancellation has no staff-role override. Participant deletion, including
  cancellation, is forbidden when submissions or messages exist.
- Contest managers control approval, disqualification, and competition status.
  Registration alone does not imply approval or permission to submit.

Registration pages become unavailable at `now >= EndTime`. The registration
service rejects requests at `now > EndTime`, so its exact-end boundary differs
from the pages.

Sources: [individual registration](../Olymp.WebApp/Components/Pages/Contests/Registration/Individual.razor),
[team registration](../Olymp.WebApp/Components/Pages/Contests/Registration/Teams.razor),
[team creation](../Olymp.WebApp/Components/Pages/Contests/Registration/CreateTeam.razor),
[registration service](../Olymp.WebApp/Services/Contests/CompetitorManagementService.cs).

## 5. Submitting solutions

The following permissions require a visible contest, an active problem, and an
account that is not disqualified. They also apply to staff accounts using the
shared interface. Staff tools are described in section 10.

| Contest state | Permanent account | Temporary participant account |
| --- | --- | --- |
| Before the start | Denied. | Denied. |
| Running, closed | Denied; sign in with the participant account instead. | Requires approval and a match to this contest. |
| Running, open | Allowed without registration. | Requires approval and a match to this contest. |
| Ended, closed | Denied. | Denied. |
| Ended, open | Allowed without registration. | Denied. |

Always store the submission under the signed-in
account (`Submission.UserId`). Never substitute a linked participant ID. To submit
as a participant or team, sign in with that account. Permanent accounts may submit
only when `IsOpen` is true, both during the contest and after its end.

Submission requests recheck the stored account, approval, disqualification,
contest availability, and contest binding. Membership does not authorize
submitting as another account. Unsent drafts are not recovered after access is
lost. An unavailable problem returns not found; a POST to an unavailable form
may be rejected with 400 Bad Request.

Only active compilers may be selected. Validation of source length, encoding,
problem membership, and compiler selection is additional to authorization.

Contest rules show participant sign-in instructions only before the contest end.
Team credentials come from the coach or organizer; individual out-of-competition
participants can obtain assigned credentials on their registration page.

Sources: [WebApp submission authorization](../Olymp.WebApp/Components/Submissions/SubmitSolutionRequirement.cs),
[attribution](../Olymp.WebApp/Components/Pages/Contests/Problems.razor),
[submission form](../Olymp.WebApp/Components/Submissions/SubmitSolutionForm.razor).

## 6. Submission history, source, and diagnostics

Submission history includes submissions made during the original
contest, including submissions of teams to which the user is linked. New practice
submissions must not replace or hide that history.

Use the same scopes in the problem workspace and the
general submissions list. A problem or contest filter narrows the selected scope.
Show the linked-participant and coached-author scope switches only when those
scopes contain visible submissions after applying the current problem/contest
filters. An empty scope can still be requested directly and displays an empty
first page; temporary accounts remain restricted to their own submissions.
The "Public" scope (`scope=all`) excludes hidden submissions and submissions
whose author has `User.IsHidden` set, including the signed-in author's submissions.
It also excludes compilation errors unless the result is hidden by freeze; frozen
submissions retain their submission fact regardless of verdict. Apply these filters
before pagination. Public rows and result visibility are identical for all viewers
who can select this scope.

| Account | Available scopes |
| --- | --- |
| Temporary `Competitor` | My submissions only, without scope switches. |
| Permanent `OlympUser` | My submissions; My contests (linked `Competitor` submissions); Public submissions with uniformly restricted information. |
| Permanent user with coached authors | The permanent-user scopes plus submissions of authors whose `User.CoachId` equals the viewer's ID. Coached authors may be `Competitor` or `OlympUser`; no coach claim is required for this scope. |

"My submissions" means `Submission.UserId` equals the signed-in account ID. Linked
participants are identified by `Competitor.Members`. The coached-author scope
uses the submission author's own `User.CoachId`; organization membership or a
relationship inferred through another account is insufficient. Scopes may
overlap. Choosing "Public" restricts the list to accepted/not accepted results,
without detailed verdicts, scores, or source links, even for owners, members, and
coaches. Independent details/download permissions remain unchanged. Temporary accounts cannot
select broader scopes through a direct URL or request parameter.

Submission scopes and pagination are preserved in the URL. Lists use 20 rows per
page, newest first, with ID descending as the tie-breaker. The default scope is
"My submissions"; "Public" is limited to the latest 200 matching submissions, including direct
URL requests.

Submission details and source require an active problem in a visible, started
contest (`AllowShowProblems`) and the following relationship. The details page
and source download use the same permission; downloads contain code only.

| Viewer relationship | Submission page and source download |
| --- | --- |
| Author | Allowed for a visible problem, regardless of `IsOpen`. |
| Linked participant member | Allowed for a visible problem only when `IsOpen` is true. |
| Author's coach, matching `User.CoachId` | Allowed for a visible problem only when `IsOpen` is true, for both participant and permanent-account authors. |
| Unrelated viewer | Denied, including in the "Public" scope and open contests. |

The linked-participant and coached-author lists remain available in closed
contests. Listing a submission does not grant access to its source or compilation
errors through the submission page and download endpoint. List result visibility
is specified separately in section 7.

After a successful submission, the workspace opens page 1 of "My submissions".

Hidden contests and inactive problems expose no shared submission lists, details,
or downloads, including to staff accounts. Expanded staff access belongs only to
the interface in section 10. Closing submissions does not remove author access
while the problem remains visible.

Permission to submit and permission to read are separate. Disqualification blocks
new submissions under section 5; it does not by itself make a problem invisible.
Existing authentication and account-lockout restrictions still apply.

Sources: [WebApp submission authorization](../Olymp.WebApp/Components/Submissions/ViewSolutionRequirement.cs),
[shared list](../Olymp.WebApp/Components/Submissions/SubmissionList.razor),
[general list page](../Olymp.WebApp/Components/Pages/Submissions/Index.razor),
[source page](../Olymp.WebApp/Components/Pages/Submissions/Details.razor),
[source download](../Olymp.WebApp/Components/Submissions/SubmissionDownloads.cs).

## 7. Results, submission feeds, progress, and freeze

Full standings include approved,
visible participants, including those out of competition, and visible submitters
with eligible submissions. Official standings exclude out-of-competition and
disqualified participants. Ordinary standings count submissions within the scheduled
contest interval; later practice does not change those results. Stored final and
official tables are displayed when available.

Ordinary standings exclude submissions sent at or
after `FreezeTime`. Earlier submissions use their current results; historical
verdicts are not snapshotted. A submission sent before the freeze can therefore
affect the table when its verdict arrives after the freeze. The freeze is not automatically lifted by reaching `EndTime`. The jury standings
view bypasses freeze; the shared interface applies the ordinary visibility rules.
Shared standings apply the same freeze to all viewers, including staff accounts.

In personal, linked-participant, and coached-author scopes, compute information
visibility per row based on the viewer's relationship to its author. The Public
scope shows accepted/not accepted only for completed checks, or the submission fact
while queued, running, or frozen, without source links or scores. Open contests
cannot be frozen, as specified in section 2.

| Viewer relationship | Closed contest, submission not hidden by freeze | Closed contest, submission hidden by freeze | Open contest |
| --- | --- | --- | --- |
| Author | Full status. | Full status. | Full status. |
| Linked participant member | Full status. | Submission fact only. | Full status. |
| Author's coach, matching `User.CoachId` | Full status. | Submission fact only. | Full status. |
| Unrelated permanent user | Accepted or not accepted only. | Submission fact only. | Accepted or not accepted only. |

Full list status includes the verdict, failed-test number where applicable, and
awarded score for contest types that use scores. It does not itself grant access
to source code or compilation logs. Those require the separate details/download
permission from section 6. Restricted results expose only whether the solution
is accepted, without the detailed failure reason, failed-test number, or score.
Submission-fact visibility exposes identifying metadata without any result,
verdict, score, or test outcome. Unrelated viewers never receive code,
compilation logs, individual test details, or internal diagnostics.

The feed freeze does not restrict an independently authorized submission details
view under section 6. Staff roles add no exception in shared views.

Problem navigation
shows the signed-in author's own progress from non-hidden submissions to active
problems, including later practice. This progress is independent of the history
scope and is not restricted by the standings freeze. Linked participant accounts'
results are not merged into personal progress.

Sources: [standings calculation](../Olymp.Domain/Standing.cs),
[general feed](../Olymp.WebApp/Components/Pages/Submissions/Index.razor),
[standings access](../Olymp.WebApp/Components/Pages/Contests/Standing.razor).

## 8. Coach relationships

Submission access uses the author's stored `User.CoachId`, for both permanent and
participant accounts. A coach claim or organization membership alone does not
grant private submission access. Coaching a team does not grant access to its
members' personal submissions unless those accounts also have a matching coach.
Name-only roster entries establish no account relationship.

The same publication, open-contest, source, and result restrictions in sections 6
and 7 apply. Registration, printing, test archives, credentials, and private
correspondence have their own rules; coach access to submissions does not broaden
them.

Sources: [author relationship](../Olymp.Domain/Models/User.cs),
[team relationships](../Olymp.Domain/Models/Competitor.cs).

## 9. Questions and jury clarifications

Common jury announcements (no user or problem association) are visible only to
temporary participant accounts in their own contest before its end.

Problem clarifications (no user association) are visible to every signed-in
viewer who can read the corresponding active problem, including after the contest
ends. Private messages never appear in announcements or clarifications.

Private correspondence is available only to the temporary participant account
for its own visible contest during the scheduled interval. Permanent accounts,
coaches, linked members, and staff roles have no additional access in this
interface. The list contains only the participant's messages about the contest
or its active problems.

Sending additionally requires current approval and no disqualification. Losing
approval or being disqualified does not remove read access during the contest.
Questions belong to the signed-in participant, concern the contest or one of its
active problems, and contain 10–4096 characters. Questions are encoded text; jury
replies and public clarifications use HTML.

Staff reads and replies use the separate staff interface; mutations require
contest management permission.

Source: [messaging](../Olymp.WebApp/Components/Pages/Contests/Questions.razor),
[WebApp announcements](../Olymp.WebApp/Components/Contests/JuryAnnouncements.razor).

## 10. Jury, administration, and service accounts

Staff permissions apply only in the separate jury/administration interface.
Shared user-facing pages and downloads retain their ordinary rules.
Saved submission IP addresses are visible only in staff submission lists and
details, including to jury members without a contest management assignment.

| Operation | Required access |
| --- | --- |
| Read contest information, participants and assigned credentials, statements, submissions, source, diagnostics, standings, and messages | Jury or administrator; includes hidden contests/accounts, unpublished and inactive problems, and results without freeze restrictions. |
| Create a contest | Jury or administrator; the creator receives management permission. New contests are hidden. |
| Edit contest settings, manage participants and credentials, import ICPC teams, answer questions, manage announcements and clarifications | Administrator or jury member with the stored `contest-manage` assignment for this contest. |
| Submit solutions from the staff task page | Jury or administrator, without a contest assignment or public publication, activity, time, or open-contest restrictions. Solutions belong to the signed-in account. |
| Run anti-cheat analysis and Runner trials | Jury or administrator. Analysis applies no participant penalties automatically. |
| Manage news, users, roles, coach claims, organizations, and compiler records; rejudge submissions | Administrator only. |
| Runner RPC | Separate API-key authentication. |

Staff operations require the complete account policy, including email migration.
Contest management uses `ManageContest`; stored assignments are rechecked for each
operation. The participant service authorizes staff mutations and registration
cancellation before writing. Cancellation uses `CancelRegistration` with the
participant as its resource and the rules in section 4. Page controls use the
same policy. Staff deletion does not bypass the history restriction.

Jury alone grants no global contest editing rights. The staff submission exception
does not expand permission to edit contests or registrations. Public and staff
submissions share compiler, source validation, and persistence rules.

Additional restrictions:

- Participant imports, bulk operations, and deletions require confirmation.
- Only completed submissions to active problems may be rejudged. Confirmation
  covers the filtered selection across pages and explicitly identifies accepted
  submissions. Submissions created after the preview are excluded; newly accepted
  submissions require renewed confirmation. Rejudging retains source, authorship,
  and check history.
- Anti-cheat selection and comparison history exclude hidden authors. A
  submission's own hidden flag does not exclude it. Similarity compares different
  authors on the same problem.
- Admin/Jury roles and coach claims are managed for permanent accounts. Lockout
  also applies to participant accounts. Granting a staff role hides the account.
  Removing roles or coach claims and blocking accounts invalidates sessions
  through the security stamp.
- Organization deletion requires confirmation and is forbidden while any account
  references it or any `coach-org` claim identifies it. These references are
  rechecked when deleting; linked accounts and claims are retained.
- Administrators may rename a participant, link or unlink permanent member
  accounts, and assign, replace, or remove its coach from the participant account
  card. These operations are allowed after the contest starts or ends and across
  organizations; the coach does not need a `coach-org` claim. An account cannot be
  linked to more than one participant in the same contest. Roster entries without
  accounts are retained; unlinking an account retains its roster name and the
  participant's submissions and messages. These exceptions apply to administrator
  editing only; ordinary registration rules are unchanged.
- Compiler records may be imported only from a connected Runner that passed
  self-testing. New records start disabled; repeated imports retain availability.
  Disabling requires confirmation and preserves existing submissions.
- Message edits reject concurrent changes to the original text or problem
  association rather than silently overwriting them.

Sources: [policies](../Olymp.WebApp/Program.cs),
[contest management](../Olymp.WebApp/Services/Authorization/ManageContestRequirement.cs),
[registration cancellation](../Olymp.WebApp/Services/Authorization/CancelRegistrationRequirement.cs),
[participant operations](../Olymp.WebApp/Services/Contests/CompetitorManagementService.cs),
[submission permission](../Olymp.WebApp/Components/Submissions/SubmitSolutionRequirement.cs).

### Known limitation: revocation in an open staff page

The two-minute security-stamp validation interval is acceptable; immediate
revocation is not required. An already open interactive staff page may continue
reading protected data after authentication is invalidated, including message and
Runner polling and list reloads. Participant lists include assigned credentials.
Initial route access is protected; mutation handlers recheck operation access.
Role-based operations retain the session validation delay.

Fixing continued read access is deferred. When addressed, loss of access must stop
further protected loads and polling and close the protected view. Operation-level
checks must remain in place.

## 11. Enforcement

- Enforce the same access decisions on pages, POST handlers, downloads, and
  interactive/API endpoints. Hidden controls do not authorize direct requests.
  Recheck operation permissions before mutations and applicable stored
  relationships and resource availability before serving protected data.
- Keep publication, submission permission, list results, and submission details
  as separate decisions. Apply scope and result restrictions on the server;
  protected data must not be returned merely because the UI hides it.
- Protected routes challenge guests and preserve login return URLs. Incomplete
  email migration redirects to account email management. Missing or unpublished
  public resources and unauthorized source requests return not found. Protected
  PDF printing denies signed-in accounts without permission. An unavailable SSR
  form handler may reject a POST with 400 Bad Request.
- Submissions include the contest end boundary; test archives require strictly
  later than the end. The registration boundary discrepancy is noted in section 4.
- HTTP caching remains at framework defaults. Any future custom caching policy
  belongs in central configuration.
