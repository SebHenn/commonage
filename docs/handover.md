# Handing the project over

What someone needs to know to keep Commonage going, and what currently depends on one person. It holds no secrets, and
should not.

## What depends on one person today

| What | Where | What to do about it |
| --- | --- | --- |
| Owning the repository, and merging | GitHub, `SebHenn/commonage` | Add a second maintainer, or transfer it to an organisation. |
| Approving a release | Required reviewer of the `release` environment | Add a second reviewer. Without an approver, no release can be published. |
| `DEPENDABOT_AUTOMERGE_TOKEN` | Actions and Dependabot secrets | A personal token. When it expires or its owner leaves, security updates wait for someone to merge them by hand. Nothing breaks. Replace it with a token of the new maintainer. |
| The security contact | [SECURITY.md](../SECURITY.md) | GitHub's private reporting goes to whoever maintains the repository. Check that someone reads it. |

## The routine

- **Changes** go through pull requests into `main`. The rules there require every check to pass, a signed-off commit
  (DCO) and a branch that is up to date. See [development.md](development.md).
- **Dependency updates** arrive from Dependabot. Patches and minor updates that pass all checks merge themselves.
  Everything else needs someone to look.
- **A release** is a tag: see "Releasing" in [development.md](development.md). The image is built for x86 and ARM, waits for
  approval, and gets a signed attestation.
- **Code scanning alerts** are in the repository's Security tab. Some are open on purpose; the reason is in each one.
- **Where the project is going** is in [roadmap.md](roadmap.md), including what was decided against and why.

## What a new maintainer should do first

1. Run the tests, then run the app and look at it: [development.md](development.md).
2. Read "How it fits together" in the same file, and the rules at the top of each milestone in the roadmap.
3. Make a release that changes nothing important, to see that the whole path still works with your own accounts.
4. Replace the token and the approver above.

## If the project stops

No archive depends on this project being alive:

- A running archive calls no service of this project, and keeps running.
- **The export** is open: JSON, a spreadsheet file and the original files, in folders. See
  [operations.md](operations.md).
- **Backups** are an ordinary PostgreSQL dump and a folder of files.
- **The images** stay available for download for as long as the registry keeps them. To be independent of that, a group
  can keep its own copy with `docker save`, or build one from the source.
- **The source** is licensed under the AGPL, so anyone may take it on, as long as they share their changes in the same way.

A group that wants to be sure should test this once: make an export, open it on another computer, and restore a backup as
described in [operations.md](operations.md).
