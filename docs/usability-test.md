# Five-task test with a real group

Before v0.1 is announced, three to five people from a real community group try five tasks on their own, while
someone watches and takes notes. The aim is to find what confuses people, not to test them. One session takes about
45 minutes per person.

## Preparation

1. Set up a demo archive in the group's language, on a laptop or online:
   `COMMONAGE_DEMO=de` (or `en`) in `.env`, then `docker compose up -d` on an empty archive. Everything in it is made
   up, so nobody's real material or data is involved.
2. Create a curator account for the session under "Accounts" and keep its setup link ready (task 3).
3. Have a phone with two or three photos on it (of anything, for example the room) for task 1.
4. Print this page's task cards (the boxes below) and one note sheet per person.
5. Ask each person for consent to take notes, and say that no names will be written down. Do not record screens or
   voices unless everyone agrees in writing.

## How to run a session

- Say: "We are testing the website, not you. If something is unclear, that is the website's fault, and exactly what
  we want to find out. Please think aloud while you work."
- Hand over one task card at a time. Do not explain the website and do not help, unless someone is stuck for more
  than three minutes; then note where and help with the smallest hint.
- Note for each task: done without help / done with a hint / not done, the time, and what the person said or where
  they hesitated.
- Afterwards ask: "What was easiest? What was hardest? What would you change?" and "Would you use this for our
  group?" (1 = no, 5 = definitely).

## The five tasks

> **1. Send in a photo (as a contributor, on a phone).**
> You have photos of a street party in Mill Street. The archive has sent you this link: *(contribution link)*.
> Send in one photo, say roughly when it was, and that you took it yourself.

> **2. Find something (as a visitor).**
> Your neighbour says there was a flood in Mill Street when she was young, in the 1960s. Find a photo of it in the
> archive.

> **3. Set up your account (as a new curator).**
> The archive group has made an account for you and sent you this link: *(setup link)*. Choose a password and log in.

> **4. Review a contribution (as a curator).**
> Someone has sent in a photo of their grandmother's shop. Look at it, add the tag "shops", and publish it so
> visitors can see it.

> **5. Handle a request (as a curator).**
> Sam, who sent in the photo of the shop, writes: "Please don't show my name any more." Make sure the archive no
> longer shows or keeps Sam's name, but the photo stays in the archive.

## What counts as success

- Every task is done without help by at least three of five people.
- Nobody needs more than one hint for any task.
- Tasks 1 and 2 (contributors and visitors, who get no training) are done without help by everyone.

## After the test

Write the findings into an issue per problem (without names), with how many people hit it. Fix what stopped people
before v0.1 is announced; plan the rest. Record the date, the group and the results summary below.

## Three more tasks, since M14

For the tester, or anyone who tries the archive between milestones. Same rules as above, on a real phone over mobile data,
not on Wi-Fi. It would help if one person is over 70 and one uses a screen reader, but nothing waits for that.

> **A. Send in photos (as a contributor, on a phone, over mobile data).**
> *(Contribution link.)* Send in three photos from your phone of anything at all. Say it was "the summer after the flood",
> and go on from whatever the form tells you.

> **B. Read a letter (as a visitor).**
> Your neighbour says the football club once wrote to its members about a new pitch. Find that letter and read it.

> **C. Look around one street (as a visitor).**
> Find everything from the 1950s in Mill Street.

What to note for task A, besides the usual:

- Which phone and browser, and how long the sending took.
- **On an iPhone: does it send photos in its usual format?** iPhones store HEIC. The browser may convert them to JPEG by itself,
  which nobody has been able to try yet. If the form refuses them, that is the first thing to report. On Android, the same
  if the phone is set to HEIC.
- Whether the chosen photos appear, and how quickly with ten large ones. Whether the page struggles with that.
- Turn on flight mode while it is sending, and off again. What happens, and what does the person do next?

## Results

**M14, November 2026, walk-through by the developer, not by real users:** tasks A to C acted out in the running demo archive, at
phone width in Chrome, with the network slowed to 600 kbit/s. No real phone, no screen reader, nobody else.

- Task A works. The wrong date was caught before anything was sent, and the three photos stayed chosen. 9.5 MB took about
  two and a quarter minutes, with the bar moving steadily, one request, and all three photos arrived. It also found four things
  the tests had not, all fixed: the mistake about the date was shown twice on the field, Stop sending was partly off the screen,
  the demo's introduction repeated the sentence about curators, and photos were too large once there were several.
- Task B cannot really be done yet: the demo's letter is typed text, with no scan to read. That is for M15.
- Task C mostly works, and shows a gap. A search for "Mill Street 1950s" finds two items, but with the year filter there are three:
  the tram, 1930–1955, was in service in the 1950s, but the search only looks for the word. That is for M19.

**v0.1, November 2026:** run with a real community group on the demo archive. All five tasks were completed
successfully, so the success criteria are met and v0.1.0 can be released.
