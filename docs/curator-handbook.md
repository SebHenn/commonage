# Handbook for curators

For the people who look after what an archive holds. You need no technical knowledge for any of this.

## Describing a photo

- **Title:** a few words that would help someone find it again: "Flood in Mill Street", not "Photo 12".
- **Date:** as precisely as you know it, and no more. "circa 1950" and "1950s" are fine. The form shows how it understood
  what you typed. Leave it empty rather than guess. It understands the languages of the material, which an admin sets under
  "Settings", and yours.
- **Description:** who and what can be seen, where and when, how you know, and how the photo came to the archive. If you
  are not sure, say so: "probably Anna Krause, left".
- **Tags:** the places, people and topics someone might look for. Before adding a new one, check whether it already
  exists under a slightly different name.

**Going back to an earlier text.** Under the form, "Earlier texts" lists each change to the title, description, date, credit,
original location and reference number: when, by whom, and what it said before ("Compare with the text now" shows it beside what the field says today). "Restore
this text" puts it back, and the text it replaces is kept in turn, so you can change your mind. A helper can read the list
but not restore. Nothing is listed for changes made before this version.

## Tidying up tags

**Tags** lists every tag with how many items have it. Open one to:

- **Say what it names:** a place, a person or a topic. Visitors find every place and every person on pages of their own,
  "Places" and "People", from A to Z, and an item shows its places and people first. Only published items count, so a name
  that only drafts have stays unseen. The People page lists names on their own, so name living people only when they agree.
- **Rename it:** the new name reaches every item at once, also in the search. A name another tag already has is refused;
  that is a merge.
- **Merge it into another tag,** for a misspelling or two words for the same thing: every item gets the other tag instead,
  and this one is gone. This cannot be undone, and the activity log notes it.
- **Delete it,** once no item has it, not even one in the bin. "Delete the unused tags" on the list does that for all of them.

## Arranging collections

**Collections** lists them in the order visitors find them. "Earlier" and "Later" move one a step; a new collection comes
last. Open one to:

- **Choose the order of its items:** recently added, oldest first or newest first. "Previous" and "Next" on an item lead
  through the collection in the same order. Undated items come last when the order goes by date.
- **Choose its cover** from the photos of its items. Visitors see that photo only while its item is published and in this
  collection; until then, and without a choice, they see the first photo. A draft's photo can be chosen ahead of publishing.
- **Delete it,** once it is empty: no items, not even in the bin, and no contribution links that send material to it.

Visitors see the years a collection's published items are from, such as "1930–1965", on its page and its card. Nothing
needs to be entered for that.

## Several photos, and letters

- **Order:** "Earlier" and "Later" move a photo, and it happens at once for visitors too. The first photo is the cover, the one seen
  in lists. "Use as cover" puts a photo first.
- **Turn right:** a quarter at a time, as often as needed. It takes a moment. Only what visitors see is turned, and the original
  stays as it came.
- **Caption:** what everyone reads under the photo, such as "Page 2". "What does it show?" is for people who cannot see it. The search
  does not look at either, so what someone might search for belongs in the description.
- **Letters and other writing:** write out the words in the description. Handwriting cannot be searched or read aloud. If you
  cannot do that yet, tick "Still needs a transcription", and "Still needs: A transcription" in the list finds them again.

Visitors can open a photo at about 3000 pixels. Whether they can is a setting for the whole archive, under "Settings".

## Recordings

A recording (MP3, M4A from a phone's voice recorder, WAV or FLAC) is added like a photo, on the item's page, by a curator or helper;
not through a contribution link. Visitors hear it with the player on the item page once the item is published. They get a copy
that has everything cut out that the file carries besides the sound: names, places, dates, the device and cover pictures. The
sound itself is exactly the original's. The original stays with curators ("Original" on the card). If the archive cannot make
sense of a file, it says "No preview could be created" and offers nothing to visitors: try saving it again from the recorder
or converting it to MP3 or WAV. OGG and Opus are not accepted yet.

Sound cannot be searched, and not everyone can listen. So an item with a recording is published only when its description holds
the transcript, or when "Still needs a transcription" is ticked. Visitors then read the description beside the player, and
search finds the words in it.

**Making a transcript on your own computer.** Nothing is sent anywhere. Install [whisper.cpp](https://github.com/ggml-org/whisper.cpp),
download one of its models (the "small" model is a good start) and run, for example,
`whisper-cli -m ggml-small.bin -l de -f interview.wav -otxt`. It needs a WAV file at 16 kHz; `ffmpeg -i interview.mp3 -ar 16000
-ac 1 interview.wav` makes one. Read the result through while listening: names and dialect words are often wrong. Paste it into the
description and untick the box once it is checked.

## Rights, credit and what only you see

Open **Rights and credit** on an item:

- **What may others do with it?** Choose honestly. "Not checked yet" is a fine answer. Choose a free licence only if whoever
  holds the rights really gave it. Someone sending in a photo agreed to publication on this website, not to reuse by others.
- **Who holds the rights?** Only curators see this.
- **Credit** is for visitors. Write one only if this person may be named publicly.

**Catalogue details** are for you: reference number, where the original is kept, who gave it to you and when, and notes. They
stay out of public pages and public search. Each photo also has a field for **what it shows**, in a sentence, for people who
cannot see it.

Visitors see the statement on the item's page, in plain words, and the credit. Without a statement of its own the archive's default
applies, and without that the page tells them to ask.

If a contributor offered reuse, the statement is already filled in with that, and the review shows what they said. It is still your
decision. Before you publish, ask yourself whether it was theirs to give: someone who has reused a photo under a free licence may go
on doing so, even if you take it back later.

Contributions from before this was possible agreed to this website only. Their picture and text stay out of link previews and the feed,
and that is not something you can change without asking them, and there is no button for it yet.

## Holding something back

Publishing does not have to mean everything at once. Two choices on an item, both on the edit page (and for many items at
once, see below), keep part of it from visitors. They apply to published items; a draft is hidden anyway.

- **What visitors get.** "Everything" is the usual case. "The record only, without pictures and recordings" shows the title,
  description, date, tags and place, and no picture, no thumbnail, no recording, no PDF, no link-preview picture and no match
  on the text inside a PDF. Use it for a record you want known about while the picture itself must stay with the family or the
  archive. A recording's **description is its transcript**, so a record-only recording still shows the transcript; if the words
  must not be public either, keep the item a draft. Contributors cannot ask for this when they send something in.
- **Public from.** A date, optional. Until the start of that day, in the archive's time zone, the item is as hidden as a draft:
  not on the page, in search, in a collection's count, in the feed or in the sitemap, and its pictures give "not found". On that
  day it appears by itself; nobody needs to be there. The date it is listed under follows the date it opens. Take the date away to
  open it at once, and it is listed as of now.

An item held back wears a badge in the list ("Record only", "Public from ..."), and the **Preview** says in its banner what visitors
will see. The list can be filtered under **Kept back from visitors**, and many items can be changed together: tick them, choose
**Set what visitors get** or **Set the public-from date** and confirm. Only curators can set either, and each change is in the item's history.

Each public item also has a machine-readable copy at its address with `.json` added, and its page carries Dublin Core tags, for
libraries and programs that collect archives. It holds only what the page shows (a record-only item lists no files, and an item
whose contributor did not agree to sharing beyond this website lists neither description nor files), and it follows the same
switch as the feed.

Feeds, the sitemap and that copy are kept for ten minutes, so a change can take that long to reach everyone. Taking something back from
people who already saw it is not possible: whoever saved a picture keeps it.

## Working through many items

Open the item list and filter it: by collection, or by what is "still needed", such as a date. Open the first item and use
**Save and next** to go through the list. Use **Preview** to see a draft as visitors will.

A year or decade typed into the search, such as "1958", "1950s" or "March 1953", also finds what is dated then, not only what
has those words. "Mill Street 1950s" finds the tram of 1930–1955 if it is about Mill Street. This is why a date on each item
matters: visitors find it through the decades on the start page too.

To change many at once, tick them, or take everything the filter found. Choose what to do: publish, add tags, set the date,
move to a collection or set the rights statement. The next page says how many items it is about and what would happen to
them, and nothing changes until you confirm. Each item that changes gets a line in its history.

Publishing many at once only takes drafts with a title. Contributions wait for a decision of their own. There is no undo for
many at once, so try a change on a few items first if you are unsure.

## A spreadsheet full of items

If the archive is already in a spreadsheet, **Import a spreadsheet** on Manage archive makes a draft of each row. Download the
template, fill in one row per item, and save it as **CSV UTF-8**. Scans can come in a ZIP file, or in a folder that whoever runs
the server has put on the server, and the `files` cell of a row names them.

First you see a dry run. It changes nothing and lists unreadable dates, collections that do not exist, files that are not there,
and rows that would be there twice. Fix the spreadsheet and check again, or import the rows that are in order and deal with the
rest later. Everything imported is a draft. If it breaks off, run it again: what is already there is left out.

## Reading an export

Admins can read the ZIP file of a Commonage export into an archive, to merge two archives or to start a new one from another:
**Read an export into this archive** on the Export page. A dry run comes first and changes nothing.

What arrives: the title, description, date, tags, collection, rights, "What visitors get" and "Public from" of each item, and its
photos. Everything arrives as a **draft**, whatever it was in the other archive, so you decide what to publish. A collection
that does not exist here is created (at most 50 in one go; import again for the rest). A tag that exists here keeps the kind it
has here.

What does not arrive, and why: the names, email addresses and consent texts of contributors, notes from visitors, internal
notes and who gave an item belong to the other archive and its promises. Items that were rejected or withdrawn there, and items
whose contributor did not agree to sharing, are left out and listed. Items in the other archive's bin are not in its export, so
they are not seen. An item that is already here (same collection, title and date) is not changed, and a photo that is already
here is not added a second time. A credit or rights holder that a curator typed into an item travels with it, so check them before you publish.
Run the same export again after a break-off: what is there is left out. That also holds for an item that arrived without all
its photos (a photo could not be stored, which the page after the import says): it stays a draft with the photos
that did arrive, and the import never adds to an item that is already here. Add the missing photos by hand, or put the draft in
the bin and run the import again. The activity log has a line for each draft that did not get all its photos.

A very large import can take longer than your browser or the server in front of it waits, and the page may then show an error or
nothing at all while the import goes on. Do not start it again: wait, and look at the activity log, where the run's line ("Imported
items from a Commonage export") appears with the number of items and photos when it is done. If the disk or the database keeps
failing, or the server is shut down, the import stops early after the items it has done and the page says how many it did not try;
choose the same file again for the rest.

## A box full of photos

**Add many photos** on the item list makes one draft from each photo. Enter what they share first: the collection, and if
you know them, the date, tags and rights statement. Then choose the photos, as many as you like.

They are sent one at a time, and you can see how far it has got. You may go on working in another tab, but keep this one open
until it is finished: the page asks before you close it.

- **If the connection breaks,** the photos that arrived are safe, and **Continue** goes on from there.
- **If the page was closed,** choose the same photos again. What the archive already has is left out, and the list says so.
- **Files the archive already has** are not added, and you get a link to where they are. **Add anyway** adds one if it really
  is a second copy you want.

The drafts have no title yet, and without one they cannot be published. **Go to the drafts without a title** leads to the list
of them, with the photo beside each one. Open the first, type a title and press Enter: that saves it and opens the next one.

For one item with several photos, such as an album or a letter of several pages, use **New item** instead. You can choose the
photos right on that form.

Without JavaScript, all the photos go in one request, at most 1 GB in all, and there is no progress to see.

## Reviewing what people send in

"Contributions waiting for review" on the management page lists what came in, oldest first. On each one:

- **Save and publish:** it is good as it is, or after you corrected the description.
- **Accept as draft:** you want to keep it, but it needs more work.
- **Reject:** spam, or not for this archive. It stays hidden until someone deletes it. An archive can also keep rejected and
  withdrawn contributions for a set time, under "Settings"; then the item page says when it goes to the bin.

A yellow note, "The archive already has this file", means a file in it is identical to one you have, with a link to that item.
It may be a second copy, or someone else's copy of the same photo. Contributors are not told.

Before publishing, look at who made it. If the contributor does not know, or the photo shows people who may not want to be
seen, ask yourself whether it should be public. When in doubt, ask them, or keep it as a draft.

If someone says a phone photo was refused because of its format, it is probably HEIC, which the archive cannot read yet. They
need to save it as a JPEG first. The form tells them so.

## Sharing a contribution link

"Contribution links" lists each link with its address. **Copy the link** puts the address on the clipboard, ready for a
newsletter or a message. **Poster to print** opens a sheet with the link's heading and introduction, a QR code that opens the
form and the address in words for those who cannot scan; print it from the page or with the browser's print command. A poster
for a closed link would send people to a page that says it is closed, so open the link first.

Only the people you give a link to know it exists. If anyone may send something in, tick "Invite visitors on the collection's
page" on the link: while it is open, the collection's public page then shows its heading and a button to the form. That gives up
the link being unlisted, so a closed link is not shown, and you cannot take the invitation back from people who already
copied the address. Someone who opens a closed link is told that it is closed.

## Asking visitors for help

On an item, tick **Ask visitors for help with this item**. Once it is published, it appears on the page "Can you help?", which
the start page links to, and its own page says the curators are looking for help. Both exist only while "Do you know more about
this?" is switched on, because that is how visitors answer. Under "Still needs", choose **Help from visitors** to see them
all; the cards at the top of Manage archive count them, and say how many are not published yet.

## Notes from visitors

Under "Settings" you can switch on "Do you know more about this?". Each published item then has a link where a visitor can
say what they know, that something is wrong, or that the item should be taken down. Name and email address are optional.

Open **Notes from visitors** to read them; removal requests come first, and an item you are editing shows how many notes it
has. Nothing a visitor sent is shown on the page: write what is worth keeping into the item, then choose **Mark as dealt
with**. Choose **Remove name and email address** once you have answered (the message stays), or **Delete** to remove a note
for good. A removal request is handled like any other, as described below.

## When someone asks for something to be removed

Take every such request seriously, and answer it quickly. It does not matter whether the person sent the photo in or is
only in the picture.

1. Open **Contributors** and search for their name or email address (it lists notes from visitors too), or for the reference they were given when they sent
   something in.
2. Open each item they ask about, and choose what fits:
   - **Remove name and email address:** the material stays, but no longer names them.
   - **Mark as withdrawn:** they take back their consent. The item is hidden at once and cannot be published again.
   - **Move to bin**, then **Delete for good** in the bin: nothing remains in the archive. Until the second step, it can
     still be restored, and the data is still there.
3. For someone who is only in a photo, there is no contribution to find: search for the item, and unpublish or delete it.
4. Tell the person what you did. Mention that backups keep older copies until they are replaced.

Everything you do here is written to the activity log, without the person's name.

## Recording something that did not come through a link

For a shoebox someone hands you in person: create the items yourself with **New item**. Note in the description who gave it
to you and when, and what they agreed to. Ask for their permission in writing if you can, and keep it with your group's
papers.

## A scanning evening with helpers

Volunteers can add and describe items without being able to publish or delete anything. An admin gives each of them an
account under **Accounts**, with the role **Helper**. A helper sees the drafts in the item list, the item form and **Add many photos**; items that are already published are for curators to change.
They can describe, add photos, turn them and mark items as needing help. They cannot publish, unpublish, move anything to
the bin, open contributions that are still to be reviewed, or see who contributed something. Everything a helper saves is a
draft until a curator publishes it.

For an evening: before, create the collection and give each helper an account (the setup link works once, and each helper
sets their own password). On the evening, let helpers add photos with **Add many photos**, then name them one after another
from the list of items without a title. Afterwards, a curator goes through the drafts, looks at what the helpers wrote, and
publishes. Disable or delete the accounts when the evening is over if the people will not come back. Helpers can still read
"received from" and the internal notes of the items they open, so do not write anything there that a volunteer should not read.

## Your login

Under **Change password** → **Log in with an authenticator app**, you can add a code from your phone to your login. Install an
authenticator app, such as Aegis, 2FAS, Google Authenticator or Microsoft Authenticator, scan the code on the page and type the six
digits the app shows. From then on the login asks for them after your password, so someone who finds out your password still
cannot log in as you. The archive may require this of everyone.

You then get ten **recovery codes**. Write them down or print them, and keep them away from your phone: without the phone, each
of them lets you log in once. If you lose your phone and the codes, an admin can turn the app off for your account.

## If something goes wrong

- **Deleted by mistake:** look in the **Bin**. Items and photos stay there for 30 days.
- **Someone else changed the same item:** the page shows both versions, and nothing is lost until you decide.
- **The archive says its disk is full:** tell the person who runs the server.
- **A photo has no preview:** the filter "A working preview" on the management page finds such items. An admin can try
  again under **System**. If it fails again, the archive cannot read the file; the original is safe and can be downloaded.
