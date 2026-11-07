# Working rules for OlympSystem

## Agreement and task scope

- Communicate with the user in Russian, briefly and to the point.
- Requests to inspect, discuss, or propose a plan mean working without changing files or data. Start implementation only after the user's instruction.
- Once agreed, complete the task and necessary checks independently; do not ask again for permission to perform already authorized actions.
- Do not expand the task with unrelated refactoring. Preserve existing uncommitted changes. Do not commit or push without an instruction.
- Do not invent product requirements or access rules. Follow agreed decisions; discuss new, debatable decisions first.
- Keep this file focused on persistent working rules, not discussion history.
- Access rules are documented in [access-control.md](docs/access-control.md).

## Interface

- Use the existing Bootstrap and its standard components and utilities.
- Do not add or modify JavaScript or CSS, including inline scripts and styles, without prior agreement with the user on the specific change. Adding a new styling library also requires prior agreement.
- First look for suitable existing components and services. Extract shared components when there is actual repetition; do not create a universal framework in advance.
- When porting behavior, compare it with the old site. Changing statement storage and moving to Markdown/LaTeX is a separate task.
- Follow the project's localization conventions: add user-facing strings to the resources for the corresponding languages.
- Use an en dash (`–`, U+2013) instead of an em dash (U+2014) in project-authored text, including UI strings, documentation, and comments.

## Running the application and browser checks

- Check the interface using the built-in browser and the tab shared by the user.
- Use the standard application addresses: `https://localhost:5001` and `http://localhost:5000`. See [launchSettings.json](Olymp.WebApp/Properties/launchSettings.json) for launch settings.
- First use an already running application. Do not start additional instances on other ports or stop processes unnecessarily. Briefly explain the reason before a necessary restart.
- Do not install Playwright, browsers, system libraries, or alternative automation tools for UI checks.
- If the built-in browser is unavailable, report the limitation and ask for access; continue independent checks without installing workarounds.
- Check the affected scenario on narrow and wide screens, including back navigation and preserving selection when relevant to the change.

## Dependencies and development data

- New dependencies and additional tool installations require prior agreement. Restoring dependencies already declared in the project is a normal part of building.
- You may create necessary test accounts in the development database through the existing Identity mechanism for sign-in and verification. Do not ask the user to sign in manually if you can do it yourself.
- Do not change existing users' passwords, roles, or data for convenient verification. Do not store passwords or other secrets in the repository.
- Permission to create development accounts does not extend to production or deleting data.

## Verification and reporting

- Follow [README](README.md), [.editorconfig](.editorconfig), and the SDK version in [global.json](global.json).
- The new interface is in [Olymp.WebApp](Olymp.WebApp); the old site is in [Olymp.Site](Olymp.Site).
- Choose checks appropriate to the change: build, existing targeted tests, and browser checks. Do not add tests that mirror the implementation for simple styling changes.
- To build the new interface, run `dotnet build Olymp.WebApp --no-restore` after restoring dependencies.
- Tests use Microsoft.Testing.Platform. Example targeted run: `dotnet test --project Olymp.IntegrationTests --filter-class 'Olymp.IntegrationTests.WebApp.ContestProblemTests' --no-restore`.
- Before finishing, check the diff for accidental changes and whitespace errors. Report what changed, which checks were actually performed, and any remaining limitations.
- Do not claim that a test or visual check passed if you could not perform it.
