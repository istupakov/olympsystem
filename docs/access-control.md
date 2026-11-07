# Access control rules

Status: agreed access rules, with implementation notes for the first interface stage.

The first WebApp stage includes contest pages, registration, statement browsing,
and material downloads. Solution submission, personal submission history, source
viewing/downloads, and compilation diagnostics are not implemented in this stage.
Their rules below describe the agreed target for a later stage.

This document covers account types, public pages, contest materials, registration,
participation, submissions, results, coaching, jury tools, and administration.
It records both the migration target and the existing implementations. It does
not authorize changing application behavior by itself.

Rule labels:

- **Confirmed**: explicitly agreed behavior from the access-control discussion;
  implementation may still be pending.
- **Current**: implemented behavior, identified as WebApp or legacy Site behavior.
- **Proposed**: a candidate rule that still needs agreement.
- **Open**: a decision required before the relevant implementation changes.

Confirmed rules define the migration target. Current behavior is retained as a
comparison where implementation still differs. Proposals remain subject to
agreement. Section 12 records resolved decisions and remaining questions.

## 1. Actors and account restrictions

| Actor | Meaning and current representation |
| --- | --- |
| Guest | No authenticated account. |
| Permanent user | An `OlympUser` using a personal account. |
| Contest participant | A separate `Competitor` account, individual or team, bound to one contest by `competitor-contest`. |
| Coach | A permanent user with one or more `coach-org` claims identifying organizations. |
| Jury member | An account with the `Jury` role; `contest-manage` grants management access to a particular contest. |
| Administrator | An account with the `Admin` role. |

**Confirmed:** roles can overlap. Coaches and permanent users share the general
interface. Temporary participant accounts may browse other visible contests and
published statements under the same publication rules as other viewers. Their
contest binding restricts participation and submission attribution, not public
read access. They cannot register as permanent users or read other participants'
private submissions. The full participant workspace is a later migration stage.

**Confirmed:** shared user-facing pages and their endpoints have no jury or
administrator overrides. Staff accounts follow the same publication, ownership,
submission, download, and freeze rules as other users in this interface. Ordinary
owner/member permissions and coach permissions still apply when the account
independently qualifies for them. Expanded staff access belongs exclusively to
the separate future jury/administration interface described in section 10.

**Current, WebApp:** protected application pages require authentication and
completion of the email-as-login transition. Account management remains available
to permanent users who still need to complete that transition. Temporary accounts
are exempt from this transition but are excluded by the `OlympUser` policy.
Passkey endpoints additionally depend on the configured feature flag.

WebApp policies include all requirements needed for their operation:

| Policy | Requirements | Usage |
| --- | --- | --- |
| Default (`[Authorize]`) | Authenticated account and completed email migration; temporary accounts are exempt from migration. | Shared protected pages and source downloads, including temporary participants; source downloads are planned. |
| `PermanentAccount` | Authenticated account without the temporary-participant claim; migration is not required. | Account management, including email migration itself. |
| `OlympUser` | Default policy plus `PermanentAccount`. | Individual registration and test downloads. |
| `Coach` | `OlympUser` plus the coach claim. | Team registration and the protected PDF printing endpoint. |
| `SignedIn` | Authentication only. | Signed-in navigation, including before email migration. |

Pages and downloads specify only the applicable complete policy, without also
adding the default policy or its prerequisites. The passkey feature policy is
separate: it checks feature availability and does not require authentication by
itself, since passkey sign-in must remain available to guests.

**Current, shared Identity:** signing in requires account confirmation. For a
temporary participant, confirmation means approval and absence of disqualification.
For a permanent user, the default email-confirmation check applies. Lockout is a
separate sign-in restriction.

`IsHidden` controls visibility; it is not equivalent to account lockout or
`IsDisqualified`. The effect of disqualification must be specified per operation.

Sources: [policy and claim names](../Olymp.Site/Names.cs),
[WebApp policies](../Olymp.WebApp/Program.cs),
[email migration](../Olymp.WebApp/Components/Account/EmailAsLoginRequirement.cs),
[account confirmation](../Olymp.Site/Services/Identity/OlympUserConfirmation.cs).

## 2. Contest state and publication

Time-based availability and actor authorization are separate checks.

| Property or flag | Meaning in current Domain code |
| --- | --- |
| `IsHidden` | Excludes a contest from ordinary visibility and public material availability. Staff exceptions differ between applications. |
| `AllowShowProblems` | The contest is not hidden and its start time has been reached. Used by the WebApp workspace and navigation; statement/source queries use the equivalent SQL predicate. |
| `AllowShowStanding` | The contest is not hidden and its start time has been reached. |
| `AllowDownloadProblems` | The contest is not hidden and either its start time or `ProblemShowTime` has been reached. Actor permissions require an additional check. |
| `AllowDownloadTests` | The contest is not hidden, is open, and has ended. |
| `AllowSubmitSolution` | The contest is not hidden, has started, and either has not ended or is open. |
| `AllowSendMessage` | The contest is not hidden and the current time is within its scheduled duration. |
| `IsOpen` | Allows permanent users to submit without contest registration, including after the scheduled end. It does not by itself mean that the contest has ended. |

**Confirmed:** `ProblemShowTime` exists to allow PDF printing before the contest
starts. It does not publish HTML statements early.

**Confirmed:** use `AllowShowProblems` for the problem navigation link, contest
problem list, and individual HTML statements. Include only active problems. This
allows reading started, non-hidden contests even when they are closed for new
submissions. Keep the sign-in requirement for the problem workspace.

**Current, WebApp:** the workspace and statements implement this publication rule.
Permanent users can read visible problems independently of submission rights.
Temporary participant accounts can also read other contests' visible problems,
public pages, and published statement PDFs. They have the same general navigation.
Submission ownership and permission to send a solution are checked separately.

Source: [Domain availability properties](../Olymp.Domain/DomainObjects.cs),
[problem workspace](../Olymp.WebApp/Components/Pages/Contests/Problems.razor),
[contest downloads](../Olymp.WebApp/Components/Contests/ContestDownloads.cs).

## 3. Public pages and downloadable materials

**Current, WebApp:** guests can read news, contacts, general rules, the visible
contest list, contest rules, public participant lists, and published standings.
Public participant lists omit hidden participants and account credentials.
Standings are published from the contest start. Hidden contests are unavailable
on these routes, including to staff in the current WebApp.

| Material | Current WebApp rule | Confirmed target |
| --- | --- | --- |
| Statement PDF after the start | Anonymous download when the contest is visible and the file exists. | Retain existing anonymous access. |
| Statement PDF before the start | Authenticated permanent account with a coach claim and a participant of this contest whose `CoachId` matches the account, from `ProblemShowTime`; visible contest and existing file required. The default email-migration requirement applies. | Both coach authorization and the stored coach relationship in this contest are required. Participant approval is not required. |
| HTML statements and examples | Signed-in account and a visible, active problem, including for temporary accounts viewing another contest. Examples contain only active, open tests. | Apply the publication rule from section 2. |
| Test ZIP | Permanent account passing the email-migration requirement, linked as a member or coach to a participant of this contest; visible, open contest strictly after its end; file must exist. | A stored participant/member or coach relationship in this contest is required. Temporary participant accounts cannot download test archives. No additional registration-approval condition is imposed. |

**Confirmed:** early printing must be authorized on the download endpoint itself.
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

**Confirmed:** test archives require a permanent user linked through
`Competitor.Members` or `Competitor.CoachId` to a participant of the requested
contest. A coach claim alone, a relationship in another contest, or a jury/admin
role is insufficient. Temporary participant accounts are excluded. A signed-in
account without the required relationship follows the ordinary access-denied
flow. These restrictions do not change public page or statement access.

Sources: [downloads](../Olymp.WebApp/Components/Contests/ContestDownloads.cs),
[participants](../Olymp.WebApp/Components/Pages/Contests/Participants.razor),
[standings page](../Olymp.WebApp/Components/Pages/Contests/Standing.razor).

## 4. Registration and participant credentials

**Current, WebApp and shared registration service:**

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
- An individual can cancel their registration before the contest starts.
- A coach can cancel their own team before the start if the team is unapproved
  or participates out of competition. They cannot cancel another coach's team.
- Contest managers control approval, disqualification, and competition status.
  Registration alone does not imply approval or permission to submit.

Registration pages become unavailable when the contest ends. The shared service
currently checks `now > EndTime`, while the WebApp pages use `now >= EndTime`;
the exact end boundary should be made consistent when these rules are implemented.

Sources: [individual registration](../Olymp.WebApp/Components/Pages/Contests/Registration/Individual.razor),
[team registration](../Olymp.WebApp/Components/Pages/Contests/Registration/Teams.razor),
[team creation](../Olymp.WebApp/Components/Pages/Contests/Registration/CreateTeam.razor),
[registration service](../Olymp.Site/Services/Contests/CompetitorManagementService.cs).

## 5. Submitting solutions

The following matrix is the **confirmed migration target**, preserving legacy
Site behavior for ordinary users. It assumes a visible contest, an active problem,
and a user who is not disqualified. The same rules apply to staff accounts using
the shared interface. Future staff tools are described in section 10.

| Contest state | Permanent account | Temporary participant account |
| --- | --- | --- |
| Before the start | Denied. | Denied. |
| Running, closed | Requires a linked, approved, non-disqualified participant marked out of competition. | Requires approval and a match to this contest. |
| Running, open | Allowed without registration. | Requires approval and a match to this contest. |
| Ended, closed | Denied. | Denied. |
| Ended, open | Allowed without registration. | Denied. |

**Confirmed attribution:** an open contest stores the submission under the signed-in
account. A closed contest uses the linked participant ID when one exists,
otherwise the signed-in ID. A temporary account is already a participant ID.
This preserves the difference between personal submissions and team submissions.

**Planned, WebApp:** the editor and POST must implement this matrix and attribution.
They must recheck the stored account, participant approval, disqualification, contest
availability, and contest binding. The form and submit shortcut are shown when
submission is allowed. A rejected POST retains the submitted code and compiler
for review, including when permission was revoked after opening the form.

Only active compilers may be selected. Validation of source length, encoding,
problem membership, and compiler selection is additional to authorization.

Sources: [legacy authorization](../Olymp.Site/Services/Authorization/ContestAuthorizationHandler.cs),
[legacy attribution](../Olymp.Site/Pages/Contests/Submits/Index.cshtml.cs).

## 6. Submission history, source, and diagnostics

**Confirmed:** personal history includes submissions made during the original
contest, including submissions of teams to which the user is linked. New practice
submissions must not replace or hide that history.

**Planned, WebApp:** owners may read personal submissions and submissions of linked
participants in the same contest when the problem is visible. Temporary accounts
can read their own participant submissions in their own contest. Owner source,
compilation errors, and source downloads use the same checks independently of
submission permission. Coaches additionally have the source-only access from
section 8. Coach-only source views omit verdicts and compilation diagnostics;
their source downloads contain code only. There are no staff-role overrides.

**Legacy Site:** source, compilation errors, and source download access is granted
to the submission owner, linked team members, and any jury member or administrator.
The source authorization handler itself does not require an open or ended contest.
Coach status by itself does not grant source access.

**Confirmed:** owner access to history, source, and compilation diagnostics is
limited to visible problems. Here, a visible problem is active and belongs to a
non-hidden contest whose start time has been reached (`AllowShowProblems`).
Closing the contest for new submissions does not revoke historical access while
the problem remains visible. Hidden contests and inactive problems do not expose
their submissions through shared owner views, including when the owner also has
a jury or administrator role. Expanded read access is reserved for the separate
future staff interface in section 10.

Permission to submit and permission to read are separate. Disqualification blocks
new submissions under section 5; it does not by itself make a problem invisible.
Existing authentication and account-lockout restrictions still apply.

**Confirmed:** use the existing stored participant, member, and coach relationships.
Do not introduce relationship changes, historical snapshots, or reassignment
workflows as part of these access rules.

Sources: [legacy source authorization](../Olymp.Site/Services/Authorization/SubmissionAuthorizationHandler.cs).

## 7. Results, submission feeds, progress, and freeze

**Current, shared standings calculation:** full standings include approved,
visible participants, including those out of competition, and visible submitters
with eligible submissions. Official standings exclude out-of-competition and
disqualified participants. Ordinary standings count submissions within the scheduled
contest interval; later practice does not change those results. Stored final and
official tables are displayed when available.

**Confirmed:** preserve the current standings freeze logic and manual unfreeze.
Ordinary standings exclude submissions sent at or
after `FreezeTime`. Earlier submissions use their current results; historical
verdicts are not snapshotted. A submission sent before the freeze can therefore
affect the table when its verdict arrives after the freeze. The freeze is not
automatically lifted by reaching `EndTime`. Legacy jury and administrator views
can bypass it; that legacy override is not part of the shared WebApp interface.
Shared standings apply the same freeze to all viewers, including staff accounts.

**Confirmed, coach views:** in shared submissions and progress views, a coach may
see results of their own teams for submissions before the freeze. These views can
provide more detail than the standings but must follow its result visibility.
For submissions at or after the freeze, show the fact of submission without the
verdict, awarded score, test outcome, or other result information. Once the freeze
is lifted, the ordinary result visibility applies again.

This uses submission time, matching the existing standings calculation; it does
not require a snapshot of verdicts at the freeze. Progress aggregates must follow
the same rule: post-freeze submissions may appear as attempts, but must not change
visible solved counts, awarded scores, or other outcome summaries while frozen.

Own submission access is a separate permission: a coach who is also a linked team
member retains the ordinary member's access. A jury or administrator role alone
adds no live-result access to the shared views. Source access has its own rule
in section 8; expanded staff visibility belongs to the future staff interface.

**Proposed (P1):** extend the limited shared feed to all signed-in permanent users.
Before the freeze it would show limited submission metadata and results; at or
after the freeze it would show only the fact of submission with identifying
metadata, without results. Source, individual test details, logs, and internal
diagnostics would not be exposed by this feed. This broader audience has not yet
been confirmed. Temporary accounts are not included in this proposed audience.

The shared feed and coach progress views are not yet implemented. The legacy
general feed has different restrictions and is not the target policy.

Sources: [standings calculation](../Olymp.Domain/Standing.cs),
[legacy general feed](../Olymp.Site/Pages/Submissions/Index.cshtml.cs),
[legacy standings access](../Olymp.Site/Pages/Contests/Standing/Index.cshtml.cs).

## 8. Coach relationships and access to code

**Confirmed:** a coach's extended access is limited to their own teams and
students. A `coach-org` claim does not grant access to other coaches' students or
source code from the same organization. A team's `CoachId` identifies its coach;
linked permanent-account members of that team provide the initial student link.

**Confirmed:** a coach may read source code submitted by their own teams only
when `Contest.IsOpen` is true. The problem must also satisfy the ordinary
publication rules. The rule uses the open flag, not merely whether the contest
has ended, and introduces no additional end-time condition. Compilation logs,
test details, and internal diagnostics are not granted by this source-code rule.

| Submission | Coach access to source |
| --- | --- |
| Own team's submission in an open contest | Allowed for a visible problem. |
| Own team's submission in a closed contest | Denied by the coach permission. |
| Another team's submission | Denied by the coach permission. |

This permission applies to submissions attributed to the team/participant account.
The earlier proposal for immediate access to personal practice submissions of
linked students is not adopted. Those personal-account submissions do not acquire
coach source access merely through a team link. Independent owner permissions
still apply. Staff roles add no source access in the shared interface.

Restricted feed access during a contest does not grant source access. Early PDF
printing and team source access are separate permissions.

Name-only team members cannot establish access to a personal account without an
explicit account link. The existing organization-wide registration list is a
registration feature; it does not authorize organization-wide source access.

**Confirmed:** keep the existing relationships. No relationship-editing feature
or historical relationship model is required by this document.

Sources: [team relationships](../Olymp.Domain/Models/Competitor.cs),
[team registration](../Olymp.WebApp/Components/Pages/Contests/Registration/Teams.razor).

## 9. Questions and jury clarifications

**Legacy Site:** sending questions uses the contest authorization handler and is
limited to the scheduled contest interval for ordinary users. Participant approval,
contest binding, disqualification, and permanent-user out-of-competition checks
follow the same branches as solution submission. Open contests allow permanent
users to send questions during that interval. Message attribution follows the
same personal-account/participant-ID rule as submissions.

The participant page loads common clarifications and questions addressed to the
signed-in user or their linked participant, for active problems or the contest as
a whole. Private questions are not part of a general public feed.

**Confirmed:** ordinary users and participants have no correspondence-history
access after the contest ends. Coaching relationships do not grant access to
private questions or replies. Shared correspondence views apply the same rules
to staff accounts. Future staff read access and jury answers belong to the
separate staff interface; mutations there require management access to the
contest. These pages have not yet been migrated.

Source: [legacy messaging](../Olymp.Site/Pages/Contests/Submits/SendMessage.cshtml.cs),
[legacy participant page](../Olymp.Site/Pages/Contests/Submits/Index.cshtml.cs).

## 10. Jury, administration, and service accounts

These interfaces remain a later migration stage and use separate protected
pages/endpoints from the shared user interface. Their permissions are not
overrides on the shared pages or download routes.

**Confirmed target for the future staff interface:** jury members have global
read-only access to contest information, including hidden contests, unpublished
materials, submissions, source, diagnostics, and results without public freeze
restrictions. Editing and other contest mutations require assignment to that
contest through `contest-manage`. The jury role alone does not grant global write
privileges. Administrators retain global management access in their dedicated
interface. None of these expanded permissions is being introduced into the
current shared interface.

The following table records the legacy implementation for comparison:

| Operation | Current legacy Site rule |
| --- | --- |
| Create a contest | Jury or administrator. |
| Edit a contest, its materials, or registrations | Administrator, or jury member with `contest-manage` for that contest. |
| Read hidden contests, unpublished statements/materials, source, and submission diagnostics | Several legacy routes/handlers grant any jury member or administrator a global override. |
| View standings without freeze restrictions | Jury or administrator. |
| Manage users, roles, coach claims, and account restrictions | Administrator. |
| Inspect checkers or run checker tools | Jury or administrator. |
| Enable/disable compiler availability through checker management | Administrator. |
| Access Runner RPC endpoints | The Runner authentication policy using a configured API key; independent of human contest permissions. |

**Implementation gap:** legacy submission and messaging handlers grant jury a
global operation override before checking contest state. Do not copy these role
overrides into shared WebApp services or endpoints. Future staff-only mutations
and their lifecycle prerequisites will be specified when that interface is built.
User-account administration and checker/compiler controls belong to staff tools;
the legacy policies above are a reference for that later stage.

Sources: [contest management scope](../Olymp.Site/Services/Authorization/ContestAccess.cs),
[legacy authorization](../Olymp.Site/Services/Authorization/ContestAuthorizationHandler.cs),
[legacy service policies](../Olymp.Site/Program.cs),
[checker management](../Olymp.Site/Pages/Checkers/Index.cshtml.cs).

## 11. Implementation requirements

- Leave HTTP caching at framework defaults for now. Any future custom caching
  policy should be configured centrally instead of setting headers in individual
  pages and download handlers.
- Evaluate resource publication, actor permission, and operation permission
  separately. Shared pages and endpoints must not grant exceptions based on
  `Jury` or `Admin`. Keep future expanded staff authorization scoped to the
  separate staff entry points, with server-side checks on those entry points.
- Enforce the same decisions in SSR pages, POST handlers, file downloads, and any
  interactive/API endpoints. Recheck authorization when accepting a mutation.
- Keep statement access, new submissions, historical metadata, source, and
  diagnostics as separate decisions. A disabled submit button must not remove an
  otherwise authorized history view.
- Domain availability properties are calculated C# properties. EF queries must
  express equivalent translatable predicates or evaluate the property after
  loading the required resource; do not assume `.Where(x => x.AllowShowProblems)`
  can be translated to SQL.
- Preserve login return URLs. Current WebApp protected routes challenge guests;
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
  existing Identity/session mechanisms; no new relationship lifecycle is introduced.
- Test direct access as well as links, and cover ownership, temporary accounts'
  public read access and participation restrictions, early PDF downloads,
  out-of-competition attribution, and the agreed
  freeze visibility rules.

## 12. Decisions and remaining questions

| ID | Status | Decision |
| --- | --- | --- |
| O1 | Confirmed | Early PDF printing requires a permanent account with a coach claim and a stored coach relationship to a participant of this contest. Participant approval isn't required. Retain existing post-start anonymous downloads; the early-print link is visible to all viewers from `ProblemShowTime`. |
| O2 | Confirmed | Publish active statements from the start of a visible contest; preserve the legacy ordinary-user submission and attribution rules. |
| O3 | Confirmed | Owner history and source access require a visible problem. Closing submission access alone does not hide the problem. |
| O4 | Confirmed | Coach privileges cover only their own teams and students, not the whole organization. |
| O5 | Confirmed | No jury/admin exceptions in the shared interface. A separate future staff interface will provide global jury read access, assigned-contest jury mutations, and global administrator management. |
| O6 | Confirmed | Preserve standings freeze and manual unfreeze. Coaches see their own teams' results before the freeze and only submission facts for later submissions in shared submissions/progress views. No verdict snapshot is required. |
| O7 | Confirmed | No ordinary correspondence-history access after the end; no coach access to private correspondence. |
| R1 | Confirmed | Keep the existing participant/member/coach relationships; add no relationship-change or historical-snapshot mechanism. |
| R2 | Confirmed | Coaches may read their own teams' source only for visible problems in open contests (`IsOpen`). |

**P1 remains a proposal:** show a limited shared submission feed to all signed-in
permanent users, applying the same pre-freeze result / post-freeze submission-fact
split. Confirm the broader audience and exact exposed fields before implementing
that extension. The separate staff interface in section 10 remains deferred;
its mutation rules will be specified when those features are migrated.
