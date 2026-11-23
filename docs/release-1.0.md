# The 1.0 checklist

What has to be true before Commonage is called 1.0, how each condition is checked, and where it stands today. It is
written down so that it is checked, not remembered. The plan behind it is in
[plan-m28-toward-1-0.md](plan-m28-toward-1-0.md), part 3.

Status written on 2026-11-23, on top of v0.5.0 with M28 parts 1 and 2 merged. **Not every condition is met, and this page says
which.** Nothing here changes how the program behaves.

## What 1.0 means

The version number does not wait for a group. 1.0 is tagged when every condition marked "machine" below is met and the
others are met or consciously accepted by the maintainer. A real group running it for a season (the first row) is a
condition for calling 1.0 **supported**, as decided in the plan (decision 4); until then the release notes say that no group has
yet run it for a season.

Where a condition is checked by a machine, it is checked by a named test or script, so the claim can be run again.

## The conditions

| # | Condition | Checked by | Status |
|---|---|---|---|
| 1 | A real group has run it for a season | A written note from the group (their name only if they agree). Not something a test can show. | **Not met.** No group has reported yet (see the roadmap). Needed for "supported", not for the number. |
| 2 | The upgrade works from every 0.x minor | `scripts/test-update.sh` in CI (job in `ci.yml`, run on every pull request). | **Partly met, and not claimed as more.** See below. |
| 3 | Export and import round trip | `ExportImportTests` in `tests/Commonage.Tests`. | **Met, with the limits below.** |
| 4 | A support policy | [SUPPORT.md](../SUPPORT.md) and the "Which versions get fixes" section of [SECURITY.md](../SECURITY.md). | **Written, proposed, not yet confirmed by the maintainer.** |
| 5 | A second person can release | [handover.md](handover.md): a second reviewer of the `release` environment, or a written handover. | **Not met.** The handover is written; no second approver exists. |
| 6 | No open `LEAK` from the privacy review, and no known accepted limit missing from `security.md` | The last dated review, below, and [security.md](security.md). | **Reviews done for M28; a final review before the tag is still to do.** |
| 7 | Accessibility and translations | `AccessibilityTests` in `tests/Commonage.BrowserTests` (axe, every page; a new page must be added) and `TranslationTests` in `tests/Commonage.Tests`. | **Met by tests.** Real phones and screen readers have not been tested (see below). |

### 2. The upgrade, in detail

What `scripts/test-update.sh` really does: it takes the **newest earlier release that has a published image** (today v0.5.0),
starts it in the production setup with the demo content, tries an update to a deliberately broken image that fails after the
database was upgraded, checks that the old version, `.env`, checkout and database all come back, refuses an unknown
release, a wrong version and an update over changed files, then updates to the code in the pull request, and checks that
updating to the version it is already on changes nothing.

What it does **not** do, so the condition stays open:

- It tests one starting version, not each of 0.1, 0.2, 0.3, 0.4 and 0.5. An upgrade from an older minor runs through the
  same migrations in order, but no machine has tried each.
- The archive in it holds the demo content, not a large or old one.
- The plan's idea of restoring a backup made by each earlier version is not built.
  `scripts/test-backup-restore.sh` backs up and restores an archive made by the **current** code only.

To meet the condition it needs either a matrix over the earlier minors in `ci.yml` (and old images that still start), or a
decision that the supported starting point is the newest minor and older ones are told to go through it, written in
`SUPPORT.md`. That is a decision for the maintainer.

### 3. The round trip, in detail

`ExportImportTests` reads an export of this app back as drafts, checks versions 4 to the current one, refuses other
versions and other files, drops a file with a wrong checksum, never looks up a name that climbs out of the folder, copes with an
entry that lies about its size and an `archive.json` that inflates, leaves out rejected, withdrawn and unconsented items, runs
twice without adding anything, and has a test that fails when the exporter gains a property the importer has no decision for.

The limits are stated, not hidden: contributor details and consent are never carried over, so an export read back is not
identical to the source, and everything arrives as a draft. That is the decision in [security.md](security.md) and the
plan, not a gap.

### 7. What no test can show

The browser tests run in Chrome. They do not replace real phones, a screen reader in use, or a person with a slow
connection. [accessibility.md](accessibility.md) says what has and has not been checked; the usability test in
[usability-test.md](usability-test.md) has not been run with a group.

## The privacy review

The agent reviews are run with the `privacy-reviewer` agent against a base ref, as the project rules ask, and the last one before a
tag is dated here. A review looks for what Commonage cannot ship: showing the public something that is not published, leaking an
original file or a contributor's personal details, deleting at once, text that only works in one language, and the security
checks in [security.md](security.md).

**Where the output is kept.** The dated summary on this page is the record: the date, what was reviewed, how many findings
of each kind were open, and what was done. The full text of a review is kept in the description of the pull request it
belongs to, and nowhere with contributor details or real archive material in it: reviews are done on code and on test
data, never on a real archive. A review that would need real material is not run; its findings are written from the code.
A finding is closed by a commit that says so, or is written into [security.md](security.md) as an accepted limit with its reason.

### Review log

| Date | What was reviewed | Result | Open `LEAK` |
|---|---|---|---|
| 2026-11 (M28) | The plan for M28 (before any code) | Fourteen gaps found and folded into the plan ("Changes after the review") | none open in the plan |
| 2026-11 (M28, part 1) | Reading Commonage's own export (the ZIP import): three reviews | Findings were fixed in the same pull request; the decisions on consent and on what arrives are in [security.md](security.md) | none known open; **to be confirmed by the maintainer** |
| 2026-11 (M28, part 2) | The public JSON and Dublin Core tags: one review | Same rules as the link preview; the cache and 404 behaviour are in [security.md](security.md) | none known open; **to be confirmed by the maintainer** |
| before the tag | A final review of the whole tree against `main` | **Not done yet** | open until done |

The two "to be confirmed" rows are written from the milestone's own record and not from the review texts, which are not in this
repository. The maintainer should check them against the pull requests (#72 and #75) before the tag, and date the final review.

## What the maintainer still has to decide or do

1. Confirm or change the proposals in [SUPPORT.md](../SUPPORT.md).
2. Decide how condition 2 is met: an upgrade matrix in CI, or a stated starting point.
3. Add a second reviewer of the `release` environment, or someone who takes over (condition 5); see [handover.md](handover.md).
4. Run and date the final privacy review, and confirm the log above.
5. Find a group, and decide when "supported" is announced.
6. Check the README status line, which still says v0.4.
