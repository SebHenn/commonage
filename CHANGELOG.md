# Changelog

All notable changes to Commonage. Versions follow [Semantic Versioning](https://semver.org/); before 1.0, a minor
version may change things that need attention when updating, and these notes say so.

## 0.8.0 (2026-11-28)

**Updating:** this version adds two migrations (people, and the conversion of person tags into people); they run when the app starts, and `update.sh` makes a backup first as always. **The conversion cannot be undone by a migration**: every tag of the kind Person becomes a person, and those that sat on an item visitors may see are carried over as "may be living" (their name appears on those items). Open the dashboard card "People from former tags" after updating and decide. Pictures need no regeneration. The export format is now 10; an export of format 4 to 9 still imports (person tags in older exports become new people seen by curators only).

### For visitors

- **People, second part.** `/people` lists the people curators chose to show, from A to Z with the number of items, and `/people/{key}` is a person's page: their name, other names a curator ticked, biography and their items. Dates appear only for someone marked as having died. An item shows a "People" line, and the search also finds an item by the names of its people. A person who may be living is on the website only: their page, and the list of people while it names them, are kept from search engines (header and meta tag), their name is not in the sitemap, the feed, the JSON of an item or its Dublin Core, and a link preview of their page shows the archive's title. Someone who has died is also in the sitemap, the JSON (`people`) and Dublin Core (`DC.subject`). A person who is hidden, in the bin or has no item visitors may see is the same "not found" as an address that never existed. The page of places moved to `/places` as its own page; the old address for people is the new page.

### For curators

- **People, first part.** Under "People" in the admin area, curators keep a list of the people who appear in the archive: a name, a sort name, dates of birth and death, a biography, other names (birth name, married name, nickname, other spelling or script, with a language) and a note that only curators see. A new person is seen by curators only. Showing a person to visitors is a second step that lists what they will see; only a person marked as having died shows dates, and every other name is hidden from visitors until it is ticked. The page of an item has an "Add a person" step that works without JavaScript: one match links at once, several ask which one is meant, none asks whether to make a new person. A note says when a public tag of the same name still exists. The personal-data search under Contributors also finds people. The preview shows a person as visitors will see them. Helpers see nothing of people. Visitors cannot see people yet.
- **Export format 10** lists people and the people of each item; the spreadsheet has a "people" column. A copy of what is public holds only people visitors may see, without address, sort name, choice or note. An import brings people in as new people that only curators see, with their other names hidden from visitors, and never matches them with people that are here. Formats 4 to 9 still import.
- **People, third part: person tags become people.** Updating turns every tag of the kind Person into a person, keeping its items (the bin included) and taking the tag off them, so "Person" is no longer a choice for a tag and the item page shows people only under People. A person whose tag sat on at least one item visitors may see on the day of the update is carried over as "may be living": visitors see their name on those items' pages and in the search, and not their dates or biography, and the person's page is kept from search engines. Every other person is carried over as seen by curators only. Nobody has chosen yet, so the dashboard has a card, "People from former tags", with the numbers (for example "12 shown without review, 3 kept hidden") and a list at `/Admin/People?review=1` where curators tick people and choose "Only curators" at once or "Has died: visitors may see them" after a question, one activity line per person; or open a person and decide there. A name that is also a topic or place tag with other accents keeps that tag. If you had already made a person of the same name yourself, you will have two people of that name afterwards: use "Merge" on the person's page to join them. One line in the activity log says the tags were converted. The update cannot be undone by a migration, so `update.sh` makes its backup first as always.
- **This is the same person as ...** on a person's page merges two people: the other keeps its data and gets the items (without doubles, the bin included) and the names (as other names that visitors do not see). The stricter choice of who may see them wins, and the confirmation says when visitors will see less. The merged person goes to the bin and keeps its items there, hidden from every page, so restoring it later brings them back. Nothing is deleted at once.
- An import of an older export (formats 4 to 9) turns its person tags into new people that only curators see, as it does for the people of format 10, and never matches them with people that are here.
- **People, fourth part: bin and split.** "Move to bin" on a person's page hides them from visitors and from the search at once and keeps them 30 days, with their other names and links; the Bin lists them under "People" to restore, or to delete for good at once (for a removal request). After 30 days they are deleted by the app. The activity log keeps no name of a deleted person. A person in the bin is not in an export, but curators still find them under Contributors.
- **Move items** on a person's page: tick the linked items and move them to another person (by number or name) or to a new person who starts as seen by curators only. Other names taken along are not shown to visitors, the page warns when visitors will see more or less of the person afterwards, and no item is changed.
- The curator handbook (English and German) has a section on people, who may see whom, merging, moving, the bin, removal requests and the review list.
- The demo archive has two people: one who has died (shown to visitors once the pages for people exist) with an other name marked for visitors, and one who stays with curators only.

## 0.7.0 (2026-11-26)

**Updating:** this version adds a migration (the story tables); it runs when the app starts, and `update.sh` makes a backup first as always. Pictures need no regeneration. The export format is now 9; an export of format 4 to 8 still imports (the import does not read stories).

### For visitors

- **Stories.** Published stories are public. `/stories` lists them with a picture and a few words, the start
  page shows the first three, and `/stories/{number}` reads one: the curator's texts, and the items of the story as cards with
  the curator's captions. An item that visitors may not see (a draft, not yet public, in the bin, withdrawn) is left out
  without a trace, and a story with nothing to read (no item visitors may see, no text with words in it) answers "not found" like an unknown page. A record-only item has no picture.
  The sitemap lists the stories. A link to a story shows its title and its summary and nothing from an item: no item picture, the archive logo as on every other page.
  Stories are not in the feed or in search. A story in the bin is gone for visitors at once. The demo archive's story is published.

### For curators

- **Stories.** Under "Stories" in the admin area, curators write a story: a title, a few words of summary, and
  parts in order, each a text or one of the items of the archive with a caption. A story can be moved up and down in the
  list, and its parts within it, with buttons that work without JavaScript and keep the place on the page. The preview shows
  it as visitors will see it: a part with an item that is not public is left out. A story with parts that visitors cannot
  see says so on the page, only here. "Move to bin" takes a story off the website at once; the bin lists it with "Restore" and
  "Delete for good", and after 30 days it is deleted for good, like items and photos (the log notes it without any name of a
  person). A removed part or text is gone at once and has no history. On the page of an item, curators see "Used in N stories"
  with links, and a note that unpublishing or binning the item takes it out of the published stories. Helpers do not see stories.
- **Export format 9** has `stories` after `items`. An export of published items has only published stories and the parts
  whose item is in it. An import counts the stories in the dry run and does not import them. The demo archive has a story.

## 0.6.0 (2026-11-23)

Updating needs no picture regeneration. Migrations run by themselves at start. **This update moves the database to
PostgreSQL 19, which takes longer than usual and needs room for a second copy of the database in the volume; read "A new
PostgreSQL version" in `docs/operations.md` before updating a real archive.**

### For people running an archive and for maintainers

- **PostgreSQL 19.** The database image moves from 18 to 19. A new PostgreSQL version cannot open the data of the old
  one, so `scripts/update.sh` now moves the archive with the backup: it stops the app, backs up, starts the new database
  beside the old data and restores into it. The old database stays in the `db-data` volume, renamed and otherwise untouched, so going back stays
  possible; once the archive has run well, remove it with the command the update prints (see "A new PostgreSQL version"
  in `docs/operations.md`). The update takes longer than usual and checks first that the volume has room for a second copy of
  the database. An update that is interrupted midway leaves `.update-in-progress` and refuses to go on until
  `sh scripts/update.sh --recover` has put the old database back. `scripts/restore.sh` has a new `--database-only`. Future
  major versions work the same way. `scripts/backup.sh` no longer recreates the database container, and
  `scripts/test-update.sh` tests the move.

- **The 1.0 checklist** is written down in `docs/release-1.0.md`: each condition, how it is checked, and where it stands. Some are
  not met yet and it says so (a group that has run it for a season, a second person who
  can approve a release). `SUPPORT.md` says which versions get security fixes and what is not supported: the newest minor only, as
  the maintainer decided on 2026-11-23. The program itself does not change.
- **The update is tested from every earlier minor.** `scripts/test-update.sh` takes a minor (`scripts/test-update.sh 0.3`),
  makes a backup with that release's own scripts and, after updating, restores that backup into the new version. A new CI job
  `update-from` runs it for 0.1 to 0.5, and it passes.

### For visitors and other programs

- **A public item can be read by a program.** Each public item page now names `/items/{number}.json` as an alternate and carries
  Dublin Core tags (`DC.title`, `DC.description`, `DC.date`, `DC.creator`, `DC.rights`, `DC.language`, `DC.subject`, `DC.identifier`,
  `DC.type`). The JSON has the title, description, the date as written plus its earliest and latest day, credit, rights, language,
  tags, collection and, when visitors get the files, their public addresses. It follows the rules of link previews: an item whose
  contributor did not agree to sharing beyond the website leaves out description and files, and a record-only item lists no files.
  Nothing a curator keeps to themselves, no contributor details and no original file names are in it. An item visitors cannot see
  answers "not found", exactly like its page. The answer is kept for ten minutes. With "Let search engines list this archive" off,
  the Dublin Core tags are left out and the JSON says `noindex` (the JSON and its link stay, as they are not a search page). After a take-down or a change to what visitors get, an answer already given can stay in a cache for up to ten minutes.

### For curators

- **Read an export into an archive.** "Read an export into this archive" on the Export page (admins) takes the ZIP file of a Commonage
  export, from this archive or another, and makes a draft of each item, with its photos, tags, collection, date, "What visitors
  get" and "Public from". A dry run comes first. What a contributor or visitor gave to the other archive stays there: names,
  email addresses, consent texts, visitor notes, internal notes and who gave an item are never read. Items that were rejected or
  withdrawn, or whose contributor did not agree to sharing, are left out and listed with the reason. An item that is already
  here (same collection, title and date) is never changed, and is not given photos it lacks. A photo that cannot be stored is counted and the item stays a draft with the others. Reads export format versions 4 to 8.
- A run that keeps failing (a full disk, a lost database) or that meets a shutdown stops and says how many items it did not try; each draft that lacks photos has a line in the activity log. A very large import may outlast the browser's wait; its result is in the activity log.
- A published item can have an **embargo**: set "Public from" on the item, and until the start of that day (in the archive's time
  zone) it is as hidden as a draft, everywhere, including its pictures. Lists, feeds and the start page put the item where it
  belongs on the day it opens. Exports of published items leave out items still under embargo (export format version 8).
- "What visitors get" and "Public from" can be set for many items at once from the item list, and the list can show only items
  shown as a record only or still under embargo. The collection cover choice says which photos visitors do not see.
- An item can be shown as a **record only**: visitors get its title, description, date, place and credit, but no picture, page
  image or recording, and search does not find what its PDFs or captions say. Choose "What visitors get" on the item's page;
  the item list shows a "Record only" badge. Exports of published items leave out the files of such items; the full export
  keeps them (export format version 7).
- The page of an item shows its **earlier texts**: when the title, description, date, credit, original location or reference
  number was changed, by whom, and what it said before, shown beside what the field says now. A curator can put an earlier text back with one button; the text it
  replaces is kept in turn. Texts from before this version have no history, and the history is not part of the export.
- AVIF and GIF photos can be uploaded, also through contribution links and as the logo. A GIF becomes one still picture:
  the first frame. The original file is kept as it was sent.
- PDF files can be uploaded by curators (not through contribution links). The first page becomes the file's cover and
  thumbnail, and every page up to 300 gets a preview picture that visitors can page through once the item is published.
  The PDF itself stays with curators, like every original.
- The text of a PDF is found by search (the first 300 pages, up to 200,000 characters), for published items only. The text
  itself is never shown. A PDF in the bin is not searched until it is restored. PDFs added before this version have no
  text; upload them again to make them searchable.
- The item page shows a PDF as its pages, one under the other, each with "Page 3 of 20".
- Recordings can be uploaded by curators (not through contribution links): MP3, M4A (the voice recorders of phones), WAV and
  FLAC. Visitors hear a copy in the item page's own player, which can jump about. Names, places, dates, cover pictures
  and everything else the file carries in tags are cut out of that copy; the sound itself is not re-encoded. The original
  stays with curators. A file whose structure the archive cannot follow is refused rather than served. OGG/Opus is not
  accepted yet. An item with a recording is published only when its description holds the transcript or "Still needs a transcription" is ticked; the handbook shows how to make one on your own computer with whisper.cpp.

### For people running an archive

- Accounts have a **name** (optional, set by an admin) and show when the person last logged in. Both are visible to admins
  only, on the Accounts pages. The activity log still names the account by its email address as it was at the time.
- A new account type, **Helper**, for volunteers: they describe items and add photos, but cannot publish, unpublish, delete,
  open contributions that are still to be reviewed, or see who contributed something. Admins choose the type under
  Accounts. The handbook has a section on a scanning evening with helpers.

- The image now contains `poppler-utils` (about 83 MB on x86) to read PDFs. A smaller image that refuses PDFs is built with
  `--build-arg WITH_PDF=false`. Each call of poppler is a separate process with a time limit (`Uploads:PdfTimeoutSeconds`).
- The export is version 6: each file lists its `pageCount`. Nothing needs regenerating; pages of earlier PDFs do not exist yet.

## 0.5.0 (2026-11-14)

### For people running an archive

Updating needs no picture regeneration. Migrations run by themselves at start.

- The export is written a batch of items at a time, so a very large archive no longer has to fit in memory. The files
  inside do not change.
- The export is version 5: it lists the tags with their kinds, each collection's position, order of items and cover, the
  visitors' notes on each item (message, name and email address only with personal data), and the "help wanted" flag. The
  settings say whether notes are switched on. Items still list their tags by name. Nothing needs regenerating.

### For visitors and curators

- A collection with nothing public in it, only drafts or items in the bin, no longer has a page of its own, and the search's
  collection filter no longer lists it. Before, anyone who knew or guessed its number could read its title and description.
- A year, decade, month or period typed into the search, such as "Mill Street 1950s", "circa 1960" or "before 1945", also finds
  what is dated then. The page says how it read the date. Curators' item list searches the same way.
- The start page shows the archive through the decades, and the search shows the decades of what it found. Each decade opens
  what it counts.
- "Places" and "People" list from A to Z what published items are tagged with, and an item shows its places and people first.
- A collection shows which years its published items are from, such as "1930–1965", on its page and its card.

- With "Do you know more about this?" switched on under Settings, a visitor can tell the curators what they know about a
  published item, that something is wrong, or ask for it to be taken down, with an optional name and email address. Nothing is
  shown publicly.
- A curator can ask visitors for help with an item. Those items are listed on a "Can you help?" page, linked from the start
  page, and say so on their own page. It exists only while "Do you know more about this?" is switched on.

### For curators

- **Import a spreadsheet** (on Manage archive): a CSV with a ZIP file of scans, or a folder on the server for more than a web page
  can send, becomes drafts with their photos and tags. A dry run comes first and changes nothing: it lists unreadable dates,
  unknown collections, missing files and what would be there twice, and only then are the rows that are in order imported. A
  template can be downloaded. After a break, run it again: what is already there is left out.
- Contribution links have a **Copy the link** button and a **Poster to print**: the heading, introduction, a QR code and the address
  in words. A curator can tick "Invite visitors on the collection's page" for a link, so that its heading and a button to it show
  on the collection's public page; off by default. Someone who opens a closed link is now told it is closed (before: "not found").

- The first page of Manage archive shows what needs attention: notes from visitors to deal with (and how many ask for removal),
  items waiting for a transcription, and items that ask visitors for help, each opening that list.

- A Notes from visitors page lists them, removal requests first. Mark them as dealt with, remove the name and email address,
  or delete them; items and Contributors show them too, and the activity log records them without the visitor.
- A Tags page: say whether a tag names a place, a person or a topic, rename it on every item at once, merge two tags into one,
  and delete tags no item has.
- Collections can be put in order, which is how visitors find them. Each collection has an order for its items (recently
  added, oldest or newest first), which "previous" and "next" on an item follow, and a cover chosen from its photos. Visitors
  only see the cover while its item is published. An empty collection can be deleted.

## 0.4.1 (2026-11-12)

Security fixes: .NET 10.0.15, and a photo that cannot stop the archive any more. `scripts/update.sh 0.4` updates; the
database change is made at the first start. Pictures do not need to be made again.

### For people who run an archive

- Security: the image runs on .NET 10.0.15, which fixes three vulnerabilities in ASP.NET Core rated high (CVE-2026-114146,
  CVE-2026-114147, CVE-2026-114149), and on an updated base system.
- Security: a photo that the preview maker cannot cope with can no longer stop the archive. Whatever goes wrong with one
  file now costs only that file's preview, and a file that took the app down three times, by using up the memory or by
  crashing the image library, is given up as failed instead of being tried again at every start. Anyone with a contribution
  link could have sent such a file. "Try again" on the System page still works for it.
- Wrong guesses of the current password on "Change password" now count towards locking the account, like wrong logins.
- A setup link's page only opens with the whole link. With the first half alone, it used to name the account's email address.
- Browsers are told to use HTTPS only for a year instead of 30 days (HSTS).
- `/health` keeps its answer for five seconds, so asking it very often costs the archive nothing.
- The app's container gives up every right it does not need (`cap_drop`, `no-new-privileges`).
- `scripts/backup.sh` and `scripts/update.sh` write backups, and the copy of `.env` made during an update, readable by
  their owner only. Backups made before are as readable as the folder they are in: `chmod -R go-rwx backups` closes them.

### Project

- Dependabot proposes a package or action update only once its release is a week old; fixes for known vulnerabilities and new
  base images do not wait.
- A weekly scan of the newest released image fails when .NET, libvips or the base image has a known vulnerability with a fix.
- Tests list every address the app answers and fail when one is open to everyone without being meant to.

## 0.4.0 (2026-11-09)

Letters that can be read, items that say what may be done with them, any language for the material, logins with an
authenticator app, and a System page. `scripts/update.sh 0.4` updates and adds the new login key to `.env`. **Before updating,
read the points in bold under "For people who run an archive"**: everyone logs in again once, rights statements curators wrote
become public, and the large photos stay off in an archive that saved its settings before. **Keep a copy of `.env` apart from the
backups** from now on, because a restore needs the same login key.

### For curators

- Each item can record what others may do with it, as a standard statement with a plain label, who holds the rights, and a
  credit. Settings has a default. A contributor's answer only suggests a statement, and only a free licence if they offered one.
- Catalogue details for curators only: reference number, where the original is kept, received from and when, internal notes.
- Each photo can say what it shows, for people who cannot see it. Visitors' pages use this as the picture's description.
- Filters for items without a rights statement or photo descriptions. A reference number typed into the search finds its item.
- Contributor details can be corrected, for example a misspelt name.
- "Contributors" also finds people named in what curators wrote.
- **Add many photos:** one draft per photo, with a shared collection, date, tags and rights statement. Photos go one at a time, with
  progress. A broken connection stops the queue and it goes on from there, and sending the same files again is safe.
- A draft from there has no title yet, can be found with a filter, and cannot be published until it has one.
- Change many items at once, ticked or all that a filter found: publish, add tags, set the date, move to a collection, set the
  rights statement. A page first says how many and what would happen.
- The archive notices identical files: on upload, on each photo, and with a note on contributions. Contributors are not told.
- A new item can take its photos on the same form, and "Save as draft" is the main button there.
- Photos added to an item appear without a reload, and typed text stays where it is. Previews come in by themselves.
- Photos can be put in order, the first being the item's cover, and turned a quarter at a time, without reloading the page and
  without losing what was typed. The original stays exactly as it came.
- Each photo can have a caption, which everyone reads under it.
- An item can be marked as still needing a transcription, with a filter to find those. The description is the place for the words
  of a letter: handwriting cannot be searched or read aloud.
- Dates are understood in the languages of the material and in the curator's own. Month names work in any language that writes them
  as words, also without accents ("Marz 1953", "12 marca 1953").

### For visitors

- Every item says in plain language whether it may be reused, and who to credit, with a link to what the statement means and to the
  archive's contact page. Without a statement, it says to ask.
- "Cite this item" gives a line to copy, and "Share" opens a phone's own list of where to share to, or copies the link.
- A link to an item pasted into a messenger shows its picture, title and the beginning of its text.
- Photos can be opened large, about 3000 pixels, to read a letter or look at a face. It is the browser's own view of a picture,
  which can zoom on every device, with nothing to load or learn.
- An item with several photos has a row of numbered thumbnails, and says "Photo 2 of 4" with each caption.
- An item leads on: the previous and next in its collection, more from the collection, and what comes from the same time. Its
  date leads to everything from that time.
- A new look: the start page opens with the group's name and welcome text, recently added photos and collections with
  pictures. System fonts, comfortable text, and cards that work the same everywhere.
- Photos are sharp on phones with fine screens and are no longer cut to fit. An item's text sits beside its photo on wide
  screens, instead of below all of them.
- Search shows active filters to remove one by one, a choice in a list applies at once, and a search without results
  offers a way on.
- The language menu is at the top of every page.
- Pages print cleanly, with their address.
- A very long tag no longer pushes a page sideways on a phone.
- Links in a list of mistakes are easier to read: their contrast was too low.
- What curators and contributors wrote says which language it is in, so screen readers read it aloud correctly, also to a visitor
  who reads the interface in another language.
- Counts read as a sentence, such as "for 30 days", get the right form in each language.

### For contributors

- The form is shorter on a phone and starts with the photos. Send takes the whole width.
- Chosen photos can be seen, added to in several goes and removed. What cannot be sent is said right away, including HEIC
  photos, which the archive cannot read yet.
- A date that cannot be understood is found before the photos are sent, and chosen photos stay chosen when something needs
  correcting.
- Sending shows how far it has got and can be stopped. A connection that goes quiet is noticed. Sending again after a lost
  connection does not make two contributions, also when the first one arrived.
- The title, date and description stay on the device for three days until they are sent, with a button to clear them. Name,
  email address and the photographer's name are never kept. Without JavaScript the form works as before.
- Someone who made a photo, or may decide for whoever did, can offer it for reuse. It is optional, and the answer is only a
  suggestion to the curators.
- The consent now also covers link previews and the archive's list of what is new, and says that copies others have made are out of
  the archive's reach.

### For people who run an archive

- **Rights statements and credits that curators have written so far now appear on public pages.** The label on the field always said
  so, but it is worth a look before updating. An item with no statement and no default for the archive tells visitors to ask.
- **Earlier contributions stay under what they agreed to,** which was "this website". Their picture and text stay out of link previews
  and the feed. Items that did not come through a contribution, and everything sent in from now on, are in both.
- An Atom feed at `/feed.xml` and a sitemap at `/sitemap.xml`, named in `robots.txt`. They and link previews are switched off together
  with "Let search engines list this archive" in "Settings".
- Older photos get the new picture size in the background, with nothing going offline. See docs/operations.md.
- **The large photos are off in an archive that has saved its settings before, because they show more than it has chosen to
  show so far.** An admin can switch them on under "Settings" and the archive then makes them in the background. In a new archive,
  or one that has never saved its settings, they are on. They take extra disk space for each photo larger than 1600 pixels.
- **If you wrote your own privacy page:** add that the contribution form keeps the title, date and description in the person's
  own browser for three days or until they are sent. The standard text now says so.
- "Settings" has the language of the material, which can be any language, and the languages dates can also be written in. Until it
  is saved, dates are read in English and German as before, and the material counts as being in the archive's language. Choose
  "Several languages, none of them most" if that is wrong for your archive.
- The export's README is in the language of the admin who exports, with an English copy beside it.
- **Logins can ask for a code from an authenticator app** as well as the password, with ten recovery codes. "Settings" can
  require it of everyone who manages the archive. An admin turns it off for someone who lost their phone, and
  `reset-two-factor` on the server does it for the last admin. See docs/operations.md.
- **The login keys in the database are now encrypted with `COMMONAGE_LOGIN_KEY` from `.env`**, so a stolen backup alone cannot
  be used to log in. `scripts/update.sh` adds the key to `.env`; after updating by hand, add one as docs/deployment.md
  describes. **Everyone logs in again once, and setup links created before stop working.** Keep a copy of `.env` apart from
  the backups: a restore needs the same key.
- **A System page** for admins: the version, the checks behind `/health`, free disk space and how fast it is used up, the
  database, the last backup and restore test, and previews that could not be made, with "Try again".
- **`/health` also checks the media folder, free disk space and the last backup.** A full disk or an old backup answers
  `Degraded`, still with HTTP 200, so set an uptime monitor to look for the word `Healthy`. A media folder that cannot be written
  to, or that misses the newest original, answers `Unhealthy` with 503, which also makes `scripts/update.sh` roll back. See
  docs/operations.md.
- "Settings" can limit how long the activity log, and rejected or withdrawn contributions, are kept. Both stay "Always" until an
  admin changes them. Rejected and withdrawn contributions then go to the bin, and for those from before this update, the time
  starts with it.
- The activity log can be filtered by who, what and a title or name.

### Project

- An item in the export can have an empty title: a draft nobody has named yet. Exports of published items never contain one.
- The export format is now version 3. In `items.csv`, the contributor's answers moved to `contributor_rights` and
  `contributor_rights_holder`. See docs/operations.md.
- The demo archive has a handwritten letter of two pages to try reading, and a story without a picture.
- Interface languages are one list, in `SupportedLanguages.cs`. Pages switch to right-to-left by language, and browser tests
  try that, and much longer text.
- Translators can work in Weblate without Git, see docs/translating.md. Hosting it is decided, and setting it up is still to do.
- A test finds every text in the code and fails when a language the interface offers has no translation for it. It also writes the
  English base file Weblate needs.
- Counts in sentences use ICU MessageFormat, with the plural rules of each language.
- The export settings include `contentLanguage` and `otherContentLanguages`, and `requireTwoFactor`.
- QRCoder (MIT) draws QR codes, as SVG in the page. The authenticator app's secret and the recovery codes are encrypted in the
  database, and the backup-and-restore test checks that no login key there can be read.
- The activity log can be scrolled with the keyboard on a phone.

## 0.3.0 (2026-11-05)

Your archive under your own name, quicker work for curators, and safe updates. Set `COMMONAGE_VERSION=0.3` in `.env`. From
this version on, `scripts/update.sh` does that for you. See docs/deployment.md. After updating, an admin should open
"Settings".

### For visitors and contributors

- The contribution form says how it understands a date while it is typed, and notices a missing agreement before chosen
  photos are lost.
- Forms that come back with a mistake list what to correct, with links to the fields. Screen readers read the message
  when a field gets the focus.
- A form cannot be sent twice by clicking twice.
- The file picker speaks the page's language, not the browser's.
- Collections and Search are always visible on phones, and everything works without JavaScript there.
- The browser's font size setting takes effect. Language buttons and tags are easier to hit on a touch screen.
- Items without a picture show a "Text" tile instead of an empty grey box.

### For curators

- "Save and next" goes through a filtered list, and "Save and add another" starts the next item in the same collection.
- The item list shows pictures, filters by collection, tag and what is still missing, and can be sorted. Waiting
  contributions come oldest first, and after a decision the next one opens.
- A review shows the photos first. Drafts can be previewed as visitors will see them.
- Every management page has the same navigation.
- Dates write the month out, because 11/4/2026 means different days to different readers.

### Your archive, your name

- Admins set the archive's name, logo, colour, language and time zone under "Settings", with no files to edit. Too light a
  colour is refused, with a similar one that works.
- About, Contact, Privacy and Imprint pages, written as plain text and linked from every page. Privacy starts from a
  description of what Commonage stores: a template to adapt, not legal advice.
- A checklist guides a new admin through setting up, and stays until everything is done.
- Contributors see whose form this is, get a reference to quote, and are told that no email follows.
- Search engines can be asked to leave the archive out. Contribution forms, logins and management pages are always left out.
- A handbook for curators, in English and German, including how to handle a removal request.
- The export format is now version 2, with the archive's settings and logo.

### For people who run an archive

- `scripts/update.sh` updates to a new release. It makes a backup first, and if the new version does not come up, it goes
  back and restores the backup. See docs/deployment.md.
- `SECURITY.md` says which versions get fixes, how soon, and how to follow releases.

### Project

- Browser tests in CI: axe on every main page at phone and desktop width, each JavaScript behaviour, and the main tasks
  without JavaScript.
- CI tests updating from the previous published release, including a failed update.
- The check for inline script and style finds all pages by itself. A new field in the data model has to be exported or be
  left out on purpose, or a test fails.

## 0.2.0 (2026-11-04)

Protection against losing material. Updating needs no steps beyond the usual ones, but set `COMMONAGE_VERSION=0.2` in
`.env` (see docs/deployment.md), and update your copy of the scripts so `scripts/backup.sh` can report its date.

### For curators

- Deleting an item or a photo moves it to the new bin, where it can be restored for 30 days. After that it is deleted
  for good by itself; "Delete for good" does it sooner, for example when someone asks for removal.
- When two people edit one item, the second one to save now sees both versions instead of silently overwriting the
  first. Nothing is lost.
- Forms warn before unsaved text is lost, for example when uploading a photo or switching language.

### For admins

- "Manage archive" shows when the last backup ran and warns when it is overdue or missing, and when the disk is
  filling up.
- "Contributors" also finds photographers and authors, and items in the bin.
- Exports without personal data now also leave out the names of photographers and authors.

### Protection

- The language cookie is only sent over HTTPS, and scripts cannot read it.
- Every package is pinned by content hash when the app is built.
- Uploads and contributions are refused before a nearly full disk can stop the database.
- One contribution link takes in at most 5 GB of files a day, and one address can try to log in 100 times an hour. Both
  can be changed.

### Project

- Dependabot's patch and minor updates merge by themselves once all checks pass; major updates still wait for
  review. GitHub Actions updates are grouped the same way as NuGet updates.

## 0.1.1 (2026-11-03)

### Running an archive

- Ready-made Docker images for x86 and ARM servers at `ghcr.io/sebhenn/commonage`, with a software bill of materials
  and a signed build attestation. `compose.production.yaml` uses them (`COMMONAGE_VERSION`), so servers no longer
  compile Commonage; updating is `docker compose pull`.
- Base images and the database and Caddy images are pinned by digest.

### Project

- Public repository setup: Dependabot, CodeQL, dependency review, OpenSSF Scorecard, a sign-off (DCO) check,
  issue and pull request templates, CODEOWNERS, and pinned GitHub Actions.
- Code of Conduct (Contributor Covenant 2.1) and a contact address for people without a GitHub account.

## 0.1.0 (2026-11-02)

The first version for real community archives.

### For visitors

- Browse collections and items with photos, dates and tags; search with accent-insensitive full-text search,
  filters by period and collection, and paging. Works on phones.
- English and German interface.

### For contributors

- Send in photos and stories through a contribution link, without an account, with rights and consent details.
  Email address optional.

### For curators

- Create, describe, publish and unpublish items; upload photos (originals stay private, previews are made without
  location data); uncertain dates such as "circa 1950" or "1950s"; tags; collections.
- Contribution links and a review workflow: publish, accept as draft, reject, delete.
- Personal data: find a contributor's contributions, remove their name and email address, mark a contribution as
  withdrawn, delete any item with its files.
- Each item shows its history.

### For admins

- Accounts for curators and admins with one-time setup links (the app sends no emails), roles, disabling and
  deleting.
- Activity log of all changes.
- Export of the whole archive as a ZIP with JSON, CSV and all original files.

### Running an archive

- Docker Compose setup with automatic database upgrades.
- HTTPS deployment with Caddy and automatic certificates (`compose.production.yaml`, docs/deployment.md).
- Backup and restore scripts, tested end to end in CI.
- Optional demo content in English or German (`COMMONAGE_DEMO`).
- Security headers with a strict Content Security Policy, length-based password rules, accessibility review
  against WCAG 2.2 AA (docs/security.md, docs/accessibility.md).
- Licensed under the GNU AGPL v3.0; every page links to the source code (`COMMONAGE_SOURCE_URL` for changed
  versions).
