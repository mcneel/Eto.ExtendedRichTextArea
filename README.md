# Eto.ExtendedRichTextArea

RichTextArea control built on Drawable for Eto.

This is a work in progress.

## Contributing a change for Rhino

This is the `rhino-9.x` branch of `mcneel/Eto.ExtendedRichTextArea`, our fork of
[picoe/Eto.ExtendedRichTextArea](https://github.com/picoe/Eto.ExtendedRichTextArea). Rhino 9 builds
this branch, where it sits inside the Rhino source tree as the submodule
`src4/DotNetSDK/Eto.ExtendedRichTextArea`. Rhino uses it for the Text and Leader editor.

Fixes go **upstream first**. Even when you found the bug in Rhino, make the change against
`picoe/Eto.ExtendedRichTextArea`'s `main` branch and let it come back to us through a merge. That
keeps this fork a copy of upstream plus a few McNeel-only commits, so nothing has to be re-applied
every time we merge. The full trip is:

> branch off `upstream/main` → pull request to `picoe/Eto.ExtendedRichTextArea` → merge `main` into
> `rhino-9.x` → bump the submodule pointer in the Rhino repo → pull request to `mcneel/rhino` `9.x`

Steps 1–4 run inside `src4/DotNetSDK/Eto.ExtendedRichTextArea` in your Rhino checkout. The examples
use the [`gh`](https://cli.github.com) CLI, but every `gh` step can be done on github.com instead.

### One-time setup

The submodule's `origin` is already `mcneel/Eto.ExtendedRichTextArea`. Add upstream alongside it:

```bash
cd src4/DotNetSDK/Eto.ExtendedRichTextArea
git remote add upstream https://github.com/picoe/Eto.ExtendedRichTextArea.git
git fetch upstream
```

### 1. Branch off upstream's main

```bash
git fetch upstream
git checkout --no-track -b <firstname>/<short-topic> upstream/main
```

Branch off `upstream/main`, **not** `rhino-9.x`. Otherwise your pull request drags along every
McNeel-only commit that upstream hasn't taken yet. Use `--no-track` so the new branch doesn't follow
`upstream/main`; otherwise a bare `git push` would try to push straight to it. Name the branch after yourself and the change,
for example `curtis/fix-ime-document-swap-crash`.

While you work on this branch:

- The Rhino repo shows `src4/DotNetSDK/Eto.ExtendedRichTextArea` as modified in `git status`. Leave
  it alone, and don't commit that pointer change on the Rhino side yet (step 5 explains why).
- Inside the Rhino tree the library builds against the Rhino tree's own Eto submodule
  (`src4/DotNetSDK/Eto.ExtendedRichTextArea.props` sets `UseLocalEto`), not the Eto NuGet package
  upstream uses. If your change needs a new Eto API, that Eto change has to land upstream first.

Before pushing, run the unit tests, then build the two projects Rhino uses. The tests build the
`Mac64` and `net8.0-windows` flavours. Rhino builds `Eto.ExtendedRichTextArea.macOS` for
`net9.0-macos15` and `Eto.ExtendedRichTextArea.Wpf` for `net48`, so code can pass the tests and
still break Rhino. Both build on a Mac.

```bash
dotnet test test/Eto.ExtendedRichTextArea.UnitTest/Eto.ExtendedRichTextArea.UnitTest.csproj
dotnet build src/Eto.ExtendedRichTextArea.Mac/Eto.ExtendedRichTextArea.macOS.csproj
dotnet build src/Eto.ExtendedRichTextArea.Wpf/Eto.ExtendedRichTextArea.Wpf.csproj
```

### 2. Commit and push the branch here

```bash
git add <changed and new files>     # -a alone misses new files, such as a new test
git commit -m "Fix crash ending IME composition after the document is replaced"
git push -u origin <firstname>/<short-topic>
```

Push to `origin`. The branch lives on `mcneel/Eto.ExtendedRichTextArea`, so anyone on the team can
pick it up.

Leave the YouTrack reference (`Fixes RH-xxxxx`) out of this commit. Upstream is a public project,
so the reference goes on the Rhino bump in step 5 instead.

### 3. Open the pull request against picoe/Eto.ExtendedRichTextArea

```bash
gh pr create --repo picoe/Eto.ExtendedRichTextArea --base main --head mcneel:<firstname>/<short-topic> --fill
```

Wait for it to be reviewed and merged upstream. The remaining steps need the commit to exist in
`picoe/Eto.ExtendedRichTextArea`.

### 4. Merge main into rhino-9.x

Once it's merged upstream, bring `main` into this branch:

```bash
git checkout rhino-9.x
git fetch origin
git merge --ff-only origin/rhino-9.x
git fetch upstream
git merge upstream/main
git diff upstream/main --stat   # what rhino-9.x still has that upstream doesn't
# run the tests and the two builds from step 1
git push origin rhino-9.x
```

These commands name the remote branches explicitly, so they work whether or not your local
`rhino-9.x` follows `origin/rhino-9.x`. If the fast-forward fails, your local `rhino-9.x` has
commits that aren't on `origin`. Sort that out before merging, since this branch should be exactly
what's on `mcneel/Eto.ExtendedRichTextArea`.

Push the merge commit straight to `origin`. We don't use pull requests on
`mcneel/Eto.ExtendedRichTextArea` itself. If the merge conflicts, it's because a McNeel-only change
touched the same code. Resolve it in the merge commit, and don't rewrite upstream's history. Often
the McNeel-only change was later made upstream in a slightly different form, and then you can take
upstream's side. The `git diff upstream/main` afterwards shows which McNeel-only differences are
left, so you can check that each one is still meant to be there.

Your topic branch can now be deleted, locally and on `mcneel/Eto.ExtendedRichTextArea`.

### 5. Bump the submodule in the Rhino repo

This works like any other Rhino submodule: a commit that moves the pointer, on its own branch, in a
pull request. Do it only now that the commit is reachable from `origin/rhino-9.x`. A pointer to a
commit that only exists on your machine or a topic branch builds fine for you and breaks the build
for everyone else, and for CI.

```bash
cd $(git rev-parse --show-superproject-working-tree)   # back to the Rhino root
git fetch origin
git checkout --no-track -b <firstname>/<short-topic> origin/9.x
git submodule update        # so only this submodule shows as changed
cd src4/DotNetSDK/Eto.ExtendedRichTextArea && git checkout rhino-9.x && cd -
git add src4/DotNetSDK/Eto.ExtendedRichTextArea
git commit          # subject = what the change does; body = "Fixes RH-xxxxx"
git push -u origin <firstname>/<short-topic>
gh pr create --base 9.x --fill
```

Branching from `origin/9.x` with `--no-track` gives you the latest `9.x` without the new branch
following it. `git submodule update` resets every submodule, this one included, to what `9.x` pins,
so check this one back out on `rhino-9.x` afterwards.

Conventions for that commit:

- The subject describes the behaviour that changed, not the mechanics. Write "Fix crash clicking
  in the text editor during IME input", not "Update Eto.ExtendedRichTextArea submodule".
- Cite the YouTrack issue in the body: `Fixes RH-99379`, or `Cite RH-99375` when the change is
  related to an issue but doesn't close it.
- Several changes can ride along in one bump. Use the subject `Eto.ExtendedRichTextArea updates` and
  give each change its own bullet in the body, with its own RH reference.
- Any Rhino-side code the change needs (for example in `AnnotationV9Panel.cs`) belongs in the same
  commit, so the new version and its first use land together.
