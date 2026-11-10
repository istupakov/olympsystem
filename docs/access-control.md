# Access control rules

Account types, publication rules, registration, submissions, results, coaching,
and staff permissions in Olymp.WebApp. This document describes application
behavior; it does not authorize changes to access rules.

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

WebApp policies include all requirements needed for their operation:

| Policy | Requirements | Usage |
| --- | --- | --- |
| Default (`[Authorize]`) | Authenticated account and completed email migration; temporary accounts are exempt from migration. | Shared protected pages and source downloads, including temporary participants. |
| `PermanentAccount` | Authenticated account without the temporary-participant claim; migration is not required. | Account management, including email migration itself. |
| `OlympUser` | Default policy plus `PermanentAccount`. | Individual registration and test downloads. |
| `Coach` | `OlympUser` plus the coach claim. | Team registration and the protected PDF printing endpoint. |
| `SignedIn` | Authentication only. | Signed-in navigation, including before email migration. |

Pages and downloads specify only the applicable complete policy, without also
adding the default policy or its prerequisites. The passkey feature policy is
separate: it checks feature availability and does not require authentication by
itself, since passkey sign-in must remain available to guests.

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

An open contest cannot have a freeze configured:
`IsOpen` implies `FreezeTime == null`. Reject changes that would violate this
invariant, whether changing `IsOpen` or `FreezeTime`. This also prevents a future
scheduled freeze from taking effect in an open contest. Opening a contest
requires clearing its freeze first or as part of the same change. The WebApp
contest editor enforces this validation; the Domain properties above do not.
Submission lists apply freeze restrictions only to closed contests, including
records that combine `IsOpen` with a non-null `FreezeTime`.

`ProblemShowTime` exists to allow PDF printing before the contest
starts. It does not publish HTML statements early.

Use `AllowShowProblems` for the problem navigation link, contest
problem list, and individual HTML statements. Include only active problems. This
allows reading started, non-hidden contests even when they are closed for new
submissions. Keep the sign-in requirement for the problem workspace.

Permanent users can read visible problems independently of submission rights.
Temporary participant accounts can also read other contests' visible problems,
public pages, and published statement PDFs. They have the same general navigation.
Submission ownership and permission to send a solution are checked separately.

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

Early printing must be authorized on the download endpoint itself.
The public `/contests/{id}/downloadproblems` endpoint is available only after the
start. Early printing uses the separate `/contests/{id}/printproblems` endpoint,
protected by the complete `Coach` policy. Before the start,
it additionally requires a participant in this contest whose `CoachId` matches
the signed-in account. A coach claim alone or a relationship in another contest
is insufficient. Participant approval isn't checked.
From `ProblemShowTime` until the start, navigation shows "Download statements for
printing (PDF)" to all viewers. Guests are asked to sign in when following the
link; signed-in accounts without permission follow the access-denied flow.
After the start, navigation shows the ordinary public PDF link. Both endpoints
serve the same file. The protected printing endpoint remains available to accounts
passing the coach policy after the start, without the additional relationship
check because the PDF is already public.
Hiding a link does not restrict a direct download request. A coach claim does not
override contest visibility. Jury and administrator roles do not bypass these
download rules in the shared interface; early printing there requires ordinary
coach authorization.

Test archives require a permanent user linked through
`Competitor.Members` or `Competitor.CoachId` to a participant of the requested
contest. A coach claim alone, a relationship in another contest, or a jury/admin
role is insufficient. Temporary participant accounts are excluded. A signed-in
account without the required relationship follows the ordinary access-denied
flow. These restrictions do not change public page or statement access.

Sources: [downloads](../Olymp.WebApp/Components/Contests/ContestDownloads.cs),
[participants](../Olymp.WebApp/Components/Pages/Contests/Participants.razor),
[standings page](../Olymp.WebApp/Components/Pages/Contests/Standing.razor).

## 4. Registration and participant credentials

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
- An individual can cancel their registration before the contest starts.
- A coach can cancel their own team before the start if the team is unapproved
  or participates out of competition. They cannot cancel another coach's team.
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

The problem page decides whether to render `SubmitSolutionForm`. The component
owns form binding, compiler selection, validation, and saving, and rechecks access
in its POST handler. Its parameters are the selected problem and the form action
URL; the page retains the submission shortcut and unavailable-access message.
They recheck the stored account, participant approval, disqualification, contest
availability, and contest binding. Membership relationships do not grant permission
to submit as another account. The form and submit shortcut are shown when
submission is allowed. Unsent drafts are not stored or recovered when access is
revoked. An unavailable problem returns not found on GET. A POST to a form that is no longer available is rejected by Blazor with
400 Bad Request. Ordinary form validation still displays the submitted values
in the current response.

Only active compilers may be selected. Validation of source length, encoding,
problem membership, and compiler selection is additional to authorization.

The authorization resource is the problem with its contest loaded.
`SubmitSolutionRequirement` has no author parameter. The handler checks problem
availability and the signed-in account's permission to submit. POST always uses
the signed-in account ID as `Submission.UserId`.

The contest rules explain participant sign-in separately for team and individual
contests, without describing `IsOpen`. Show the entire sign-in section, including
its heading, only while `now < EndTime`. Team credentials come from the coach or
organizer; coaches can view their teams' assigned credentials on the team
registration page. Individual out-of-competition participants use their own
registration page; official participants receive credentials from the organizer.

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
The "All" scope excludes submissions whose author has `User.IsHidden` set,
including both permanent accounts and participant/team accounts, except for
the signed-in author's own submissions. Apply this filter before pagination.

| Account | Available scopes |
| --- | --- |
| Temporary `Competitor` | Own submissions only, without scope switches. |
| Permanent `OlympUser` | Own submissions; linked `Competitor` submissions when a relationship exists; all visible submissions with restricted information for unrelated authors. |
| Permanent user with coached authors | The permanent-user scopes plus submissions of authors whose `User.CoachId` equals the viewer's ID. Coached authors may be `Competitor` or `OlympUser`; no coach claim is required for this scope. |

"Own" means `Submission.UserId` equals the signed-in account ID. Linked
participants are identified by `Competitor.Members`. The coached-author scope
uses the submission author's own `User.CoachId`; organization membership or a
relationship inferred through another account is insufficient. Scopes may
overlap. Choosing "All" does not reduce owner/member/coach permissions or grant
additional access to another author's private data. Temporary accounts cannot
select broader scopes through a direct URL or request parameter.

All scopes use 20 submissions per page, ordered by
commit time descending and then ID descending. The "All" scope is limited to
10 pages (the latest 200 submissions after applying problem/contest filters),
including direct URL requests. Other scopes have no depth limit. Detect the next
page without counting the entire history; do not offer a next page after page 10
in "All". The default scope is "Own". Scope and page are stored in the URL.

A submission's author (`Submission.UserId`) can view its
source, status, and compilation errors whenever the problem is visible,
independently of `Contest.IsOpen` and permission to submit. This includes a
temporary account viewing submissions attributed to that participant account.
Linked members can view their participant's submission details only when
`Contest.IsOpen` is true. A coach can view any author's submission details under
the same condition when that author's `User.CoachId` matches the viewer's ID.
The stored member/coach relationship is sufficient; no coach claim is required.
The submission page and source download use the same `ViewSolutionRequirement`;
source downloads contain code only.
There are no staff-role overrides.

| Viewer relationship | Submission page and source download |
| --- | --- |
| Author | Allowed for a visible problem, regardless of `IsOpen`. |
| Linked participant member | Allowed for a visible problem only when `IsOpen` is true. |
| Author's coach, matching `User.CoachId` | Allowed for a visible problem only when `IsOpen` is true, for both participant and permanent-account authors. |
| Unrelated viewer | Denied, including in the "All" scope and open contests. |

The linked-participant and coached-author lists remain available in closed
contests. Listing a submission does not grant access to its source or compilation
errors through the submission page and download endpoint. List result visibility
is specified separately in section 7.

The problem workspace and `/submissions` reuse the same
list component for scopes, per-row results, links, and pagination. The details page
and download handler check direct `User.CoachId` relationships for both author
types. After a successful submission, the workspace opens page 1 of "Own".

Owner access to history, source, and compilation diagnostics is
limited to visible problems. Here, a visible problem is active and belongs to a
non-hidden contest whose start time has been reached (`AllowShowProblems`).
Closing the contest for new submissions does not revoke the author's access while
the problem remains visible. For a linked member or coach, viewing another
account's submission details additionally requires `IsOpen`. Hidden contests and
inactive problems do not expose submissions through the shared lists, details
pages, or downloads, including when the viewer also has a jury or administrator
role. Expanded read access is reserved for the separate staff interface
in section 10.

Permission to submit and permission to read are separate. Disqualification blocks
new submissions under section 5; it does not by itself make a problem invisible.
Existing authentication and account-lockout restrictions still apply.

Use the stored participant, member, and coach relationships.
Do not introduce relationship changes, historical snapshots, or reassignment
workflows as part of these access rules.

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

Compute information visibility per row,
based on the viewer's relationship to its author, independently of the selected
list scope. Open contests cannot be frozen, as specified in section 2.

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

Own submission access is a separate permission: the author retains access
regardless of `IsOpen`; linked team members and coaches require `IsOpen` to view
another account's submission details. A jury or administrator role alone
adds no live-result access to the shared views. The shared-feed freeze does not
restrict an authorized submission details view under section 6. Expanded staff visibility belongs to the staff
interface.

All signed-in permanent users may select the "All" scope,
subject to the publication rules and per-row information limits above. Temporary
participant accounts remain limited to their own submissions. All scopes use the
same relationship-based permission checks; switching scopes cannot bypass result
or source restrictions.

Problem navigation
shows the signed-in author's own progress from non-hidden submissions to active
problems, including later practice. This progress is independent of the history
scope and is not restricted by the standings freeze. Linked participant accounts'
results are not merged into personal progress.

Sources: [standings calculation](../Olymp.Domain/Standing.cs),
[general feed](../Olymp.WebApp/Components/Pages/Submissions/Index.razor),
[standings access](../Olymp.WebApp/Components/Pages/Contests/Standing.razor).

## 8. Coach relationships and access to submissions

A coach's extended access is limited to authors whose `User.CoachId`
equals the coach's account ID. This includes both `Competitor` and `OlympUser`
authors. A `coach-org` claim does not grant access to other authors' private data
from the same organization and is not required for this stored-relationship
permission. Being a member of a coached team does not by itself grant the coach
access to submissions attributed to that member's personal account; the personal
account must have its own matching `CoachId`.

A coach may view their coached authors' submissions, including
source code, status, and compilation errors, only when `Contest.IsOpen` is true.
The stored `CoachId` relationship is sufficient; no `coach-org` claim is required
for this access. The problem must also satisfy the ordinary publication rules.
The rule uses the open flag, not merely whether the contest has ended, and
introduces no additional end-time condition. The submission page
and source download use the same permission as for owners and linked members;
no separate diagnostics permission is required.

| Submission | Coach access to submission details and source |
| --- | --- |
| Coached participant or permanent account's submission in an open contest | Allowed for a visible problem. |
| Coached participant or permanent account's submission in a closed contest | Denied by the coach permission; limited list results remain available under section 7. |
| Author without a matching `User.CoachId` | Denied by the coach permission; the "All" scope can expose restricted list information. |

Independent author and participant-member permissions still apply. Staff roles
add no submission access in the shared interface. Registration, early PDF printing,
test archives, credentials, and correspondence retain their separately specified
rules; extending the coached-author submission scope does not broaden those
permissions.

Restricted feed access during a contest does not grant submission details access.
Early PDF printing and coached-author submission access are separate permissions.

Name-only team members cannot establish access to a personal account without an
explicit account link. The existing organization-wide registration list is a
registration feature; it does not authorize organization-wide source access.

The application uses stored relationships. No relationship-editing feature
or historical relationship model is required by this document.

Sources: [author/coach relationship](../Olymp.Domain/Models/User.cs),
[team relationships](../Olymp.Domain/Models/Competitor.cs),
[team registration](../Olymp.WebApp/Components/Pages/Contests/Registration/Teams.razor).

## 9. Questions and jury clarifications

Common jury announcements (`UserId == null`
and `ProblemId == null`) appear below the navigation in the temporary participant's
own contest header, including on the submissions page. They are hidden after the
contest ends and are not displayed to permanent accounts, including in open
training contests.

Published problem clarifications (`UserId == null` and a matching `ProblemId`)
appear after the statement and examples, before the submission form. They remain
available after the contest ends to every signed-in viewer who can read that
active problem in a visible, started contest. Messages for other or inactive
problems are excluded. Both blocks display non-empty jury text as HTML, newest first; empty blocks are omitted. Messages addressed to a
specific user are excluded from both blocks, including the viewer's own messages.
Use the existing refresh/navigation behavior without adding another refresh
button or automatic updates.

Private questions and replies have a separate
page linked from the temporary participant's own contest navigation during the
scheduled contest interval. Only that contest's temporary participant account
can read its own correspondence. Permanent accounts, including coaches and users
in open training contests, cannot use this page. Before the start, after the end,
and in hidden contests, it is unavailable and its navigation link is hidden.

Sending requires participant approval and no disqualification. Disqualification
or loss of approval does not remove access to existing correspondence during the
tour, but prevents new questions. Both GET and POST load the current stored
participant and contest; POST also checks the selected topic and current contest
time. Questions are attributed to the signed-in participant, never a linked
account. A question can concern the contest as a whole or an active problem in
that contest. Questions must contain 10–4096 characters.

The page shows only messages addressed to that participant, for the contest as a
whole or its active problems. Other participants' messages and public announcements
are excluded from the private list. Questions are encoded text; jury replies use
HTML. Newest questions appear first, with pending replies explicitly marked.
Refresh is manual, without unread tracking or automatic updates.

Coaching and membership relationships do not grant private correspondence access.
Staff roles add no exceptions in this interface. Staff read access and jury
answers use the separate staff interface; mutations require management access
to the contest.

Source: [messaging](../Olymp.WebApp/Components/Pages/Contests/Questions.razor),
[WebApp announcements](../Olymp.WebApp/Components/Contests/JuryAnnouncements.razor).

## 10. Jury, administration, and service accounts

Staff tools use separate protected pages from the shared user interface. Their
permissions are not overrides on the shared pages or download routes.

`/jury/contests` lists all contests, including hidden ones.
Jury members and administrators can create contests; creation assigns
`contest-manage` to the creator in the same transaction. Editing the basic fields
requires an administrator or a jury member with that stored assignment. The
assignment is read from the database when opening and saving the editor, so a
creator can continue editing without refreshing their sign-in claims. Roles use
the current authentication state. New contests are hidden by default.

The Interactive Server editor accepts a start date/time in the server time zone,
a duration, optional PDF printing advance before the start, and optional freeze
advance before the end. Moving the start preserves these intervals and shifts
all calculated dates. Dates are stored as absolute timestamps. Problem creation
and uploads are not available in the web editor.

`/admin/news` allows administrators to create, edit, and
delete news. Deletion requires confirmation in the editor and an administrator
check in its handler. News editing includes titles, HTML text, and publication
dates in the server time zone. News appears
immediately after saving, including with a future publication date. Editing does
not automatically change that date. Both editors support HTML previews and check
operation permissions again on save.

Contest staff navigation links settings, tasks, participants, submissions,
standings, messages, and anti-cheat reports. Jury and administrators can view
all contests' participants, including hidden accounts and assigned logins/passwords. Participant
mutations require an administrator or the current stored `contest-manage` assignment.
They use the participant service for import, credential issuance,
approval, competition status, disqualification, and deletion restrictions. Imports,
bulk operations, and deletion require confirmation.

The staff submission list includes every submission to the selected contest's
problems, including hidden accounts, hidden submissions, inactive problems, and
practice submissions. Author, problem, compiler, status, date filters, and pagination
are stored in the URL. `/jury/submissions/{id}` and its `/download` endpoint grant
jury and administrators source access independently of shared-page publication or
relationship rules. The staff details page additionally shows full check history
and logs. Source presentation and compilation diagnostics are shared with the
ordinary submission page; each interface applies its own authorization.
The shared table receives already filtered result/source visibility from each
interface. Staff author links filter by exact account ID within the same contest.
The staff list supports All, Own, Participants, and Jury author scopes,
combined with the other URL filters. Jury means the author's `IsHidden` flag,
including hidden participant accounts; it does not depend on assigned roles.
The task workspace includes paginated Own/Jury submission lists using
the same author filtering and row projection. Successful submission stays on the
selected task, clears the source while retaining the compiler, and refreshes Own
on its first page. Source links preserve the task, scope, and list page on return.
Report source links open a new tab; a return-to-list link appears only when the
source was opened with a valid list return URL, preserving that list's filters.

`/jury/contests/{id}/problems` shows all tasks, including inactive tasks in hidden
or unpublished contests, their existing HTML statements, open active examples,
and common problem clarifications. PDF download is not provided in this view.
Clarification presentation is shared with the ordinary problem statement; private
messages are excluded. Each interface loads its own authorized statement data.

Any jury member or administrator can submit solutions from the staff task page, without a `contest-manage` assignment or
publication, activity, start/end, or open-participation restrictions on the task
and contest. This exception applies to submitting solutions; editing public
contest data and registrations still requires the existing management permission.
Solutions belong to the signed-in account and use the existing Runner queue.
The queue also accepts inactive problems for authors with a current Jury/Admin
role; ordinary authors still require an active problem.
Staff and public submission handlers share compiler availability, source length
and encoding validation, and persistence. Both submission operations use `SubmitSolutionHandler`; the staff
operation explicitly sets `SubmitSolutionRequirement.ForJury`, which checks the
current Jury/Admin role instead of public contest availability rules.

Contest anti-cheat examines submissions to that contest's problems from both
permanent and temporary accounts. Hidden **authors** are excluded; a submission's
own hidden flag does not exclude it. Similarity compares different authors on the
same problem. Additional jury tools support analysis by author, by period against
problem history, direct comparison of two submissions, and AI indicators for a
single submission. Hidden authors are excluded from selection and comparison
history in these tools as well. Analysis supports cancellation and does not apply
participant penalties automatically.

`/admin/users` and `/admin/users/{id}/edit` are administrator-only. They provide
search and filters, account details, Admin/Jury roles, coach organization claims,
visibility, login/email synchronization, and account lockout controls through
Identity. Role and coach controls apply to permanent accounts; lockout controls
also apply to participant accounts. Granting Admin/Jury hides the account. Removing roles/coach claims and blocking accounts
updates the security stamp through the existing session invalidation mechanism.

`/admin/organizations` and its new/edit pages are administrator-only Interactive
Server pages. Administrators can search, create, and edit short/full organization
names and the existing team-name prefix. Deletion requires explicit confirmation
showing the stored organization name and is allowed only when no user/participant
has that `OrganizationId` and no `coach-org` claim references the organization.
The UI shows linked-account and distinct coach counts. Both references are checked
again in the delete statement, so connections added after opening the editor also
prevent deletion. Linked accounts and coach claims are retained.

`/jury/contests/{id}/standing` provides full and official
standings for hidden contests without freeze restrictions, reusing the existing
standings calculation and table. Saved final/official tables remain available.
The public standings retain their existing publication and freeze rules.

`/jury/submissions` provides the same staff submission list across contests,
including a contest filter. All author, compiler, verdict, problem, and date
filters apply before pagination. Source links preserve global or contest list
context on return.

Administrators can rejudge the applied filtered selection across all pages.
Confirmation shows the eligible count and a separate warning listing the count of
accepted submissions. Only completed submissions to active problems are eligible;
queued and checking submissions are excluded. The selection excludes submissions
created after the preview. Newly accepted submissions require renewed confirmation
and cannot be silently included without it. Queueing clears current scores and
updates the status and modification time, retaining source, authorship, and check
history. The final notice reports the actual number queued. Jury alone cannot
invoke this action. This is separate from the deferred contest preparation tools.

`/jury/contests/{id}/messages` shows questions, replies, announcements, and problem
clarifications, with problem/author/answer-state filters and pagination. Any jury
member or administrator can read; mutations use the current stored contest
management permission. Replies can be edited or cleared without deleting the
question. Announcements and clarifications can be created, edited, or deleted
with confirmation. Existing HTML is retained and previews are available. Updates
check the original text and problem association to prevent silent overwrites of
another editor's changes. Auto-refresh every ten seconds preserves drafts.
Sound is explicitly enabled by a browser click and applies only while this page
is open. New question IDs trigger one signal per refresh; initial history and
filter changes do not trigger sound. The page owns and disposes its polling and
collocated audio module; suspended/disconnected browser pages may delay alerts.

`/jury/runners` has Runner and compiler views. It retains IDs, names, connection
times, configurations, compiler/version descriptions and errors, self-test state,
all test outcomes and logs, and trial code execution. Trials are available to
jury and administrators with 1-second/128-MB limits; input is retained on errors.
Runner information refreshes every ten seconds. Compiler availability
in the database is shown separately from availability on connected Runner.
Only administrators may add or enable/disable compiler records. New records are
imported from a connected Runner that passed self-testing and start disabled;
repeated imports retain the existing record and its availability. Disabling a
compiler requires confirmation and retains existing submissions. Runner RPC uses
a separate API-key policy. Deployment and trusted forwarded-header configuration
are described in [deployment.md](deployment.md). Only public HTTP solution
submissions record their server-resolved IP in `CommiterInfo`; jury submissions
leave it empty. Contest preparation has no web editor.

Jury members have global read-only access to contest information, including hidden contests, unpublished
materials, submissions, source, diagnostics, and results without public freeze
restrictions. Editing and other contest mutations require assignment to that
contest through `contest-manage`, except for the explicitly allowed staff solution
submissions described above. The jury role alone does not grant global editing
privileges. Administrators retain global management access in their dedicated
interface. These expanded permissions apply only to the staff interface.

Sources: [stored management permission](../Olymp.WebApp/Components/Staff/StaffContestAccess.cs),
[staff submission permission](../Olymp.WebApp/Components/Submissions/SubmitSolutionRequirement.cs),
[service policies and RPC](../Olymp.WebApp/Program.cs),
[Runner tools](../Olymp.WebApp/Components/Pages/Staff/Jury/Runners.razor).

## 11. Implementation requirements

- Leave HTTP caching at framework defaults for now. Any future custom caching
  policy should be configured centrally instead of setting headers in individual
  pages and download handlers.
- Evaluate resource publication, actor permission, and operation permission
  separately. Shared pages and endpoints must not grant exceptions based on
  `Jury` or `Admin`. Keep expanded staff authorization scoped to the
  separate staff entry points, with server-side checks on those entry points.
- Enforce the same decisions in SSR pages, POST handlers, file downloads, and any
  interactive/API endpoints. Recheck authorization when accepting a mutation.
- Keep statement access, new submissions, list metadata/results, and submission
  details as separate decisions. The details page and source download share the
  submission viewing permission; list results use the visibility matrix in
  section 7. A disabled submit button must not remove an otherwise authorized
  history view. Do not return hidden verdicts, scores, or diagnostics in list
  data merely because the UI omits them.
- Enforce scope restrictions on the server. Temporary participants have only
  their own scope. Broader permanent-user scopes do not expand per-row
  permissions. Check coached authors through `User.CoachId` for both account
  types, without introducing an additional coach-claim requirement.
- Reject contest changes that combine `IsOpen` with a non-null `FreezeTime`,
  including a scheduled future freeze. Preserve the existing standings algorithm
  and manual unfreeze behavior. The contest editor enforces this validation.
- Domain availability properties are calculated C# properties. EF queries must
  express equivalent translatable predicates or evaluate the property after
  loading the required resource; do not assume `.Where(x => x.AllowShowProblems)`
  can be translated to SQL.
- Preserve login return URLs. Protected routes challenge guests;
  email-migration failures redirect to account email management. Missing and
  unpublished public resources generally return the localized not-found page.
  Early PDF requests challenge guests and deny signed-in non-coaches through the
  existing access-denied flow. Unauthorized source requests return not found;
  SSR may reject an unavailable POST form handler with a bad-request
  response before the component's not-found response is rendered.
- Define start/end/freeze boundary comparisons consistently. Existing properties
  use inclusive end boundaries for submissions and strict end boundaries for test
  downloads; registration currently has the discrepancy noted in section 4.
- Recheck the applicable stored relationships and resource availability when
  serving protected data. Account restrictions and claim changes follow the
  Identity/session mechanisms.
- Test direct access as well as links, and cover ownership, temporary accounts'
  public read access and participation restrictions, early PDF downloads,
  out-of-competition attribution, and freeze visibility rules.
