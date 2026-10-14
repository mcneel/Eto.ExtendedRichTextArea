# Eto.ExtendedRichTextArea — Agent Guide

RichTextArea control built on an Eto Drawable. Keep this file compact, because it loads every
session. Record only durable, non-obvious facts.

**Contributing from the Rhino tree:** the upstream-first workflow (branch off `upstream/main` → PR
to `picoe/Eto.ExtendedRichTextArea` → merge into `rhino-9.x` → bump the submodule in the Rhino repo)
is documented for humans in `README.md`. Follow it there rather than pushing straight to
`rhino-9.x`.

- `origin` is `mcneel/Eto.ExtendedRichTextArea` (the fork), `upstream` is
  `picoe/Eto.ExtendedRichTextArea`. Upstream's mainline is `main`, not `develop`.
- Branch with `--no-track` so a topic branch never follows `upstream/main` or `origin/rhino-9.x`.
- Never push `rhino-9.x` or open the Rhino bump without the user's go-ahead. The bump must point at
  a commit already on `origin/rhino-9.x`.
- Keep `RH-xxxxx` references out of upstream commits. They belong in the Rhino bump commit.
- Passing tests isn't enough: Rhino builds the `macOS` (`net9.0-macos15`) and `Wpf` (`net48`)
  projects, which the tests don't. Build both (README step 1) before pushing or bumping.

## Running unit tests

```bash
dotnet test test/Eto.ExtendedRichTextArea.UnitTest/Eto.ExtendedRichTextArea.UnitTest.csproj --filter "FullyQualifiedName~CompositionTests"
```

- Don't pass `-f`. The test project targets `net8.0` on macOS and `net8.0-windows` on Windows.
- Inside the Rhino tree it builds against the sibling `src4/DotNetSDK/Eto` submodule, not the Eto
  NuGet package.
- Private members are reached by reflection (for example the `_drawable` field), because the
  strong-named assembly has no `InternalsVisibleTo` for the tests.
