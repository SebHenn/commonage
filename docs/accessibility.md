# Accessibility

Commonage aims at WCAG 2.2 level AA. Contributors and visitors include older people, people on small phones and
people using screen readers or only a keyboard.

## What the v0.1 review checked

- **Automated check** with [axe-core](https://github.com/dequelabs/axe-core) 4.10 (WCAG 2.0, 2.1 and 2.2 A/AA rules
  plus best practices) on every page: start page, collection, item, search with and without filters, contribution
  form, login, error page, and all curator and admin pages. In German at phone width (390 px) and in English at
  desktop width (1280 px), on the demo archive. Result: no violations.
- **Layout**: no page scrolls sideways at 390 px; text reflows; nothing depends on hovering.
- **Structure**: one `h1` per page, headings in order, landmarks (header, main, footer), a "Skip to content" link as
  the first thing the keyboard reaches, breadcrumbs as a labelled `nav`, the page language in `<html lang>`.
- **Forms**: every field has a visible label; groups of radio buttons and checkboxes are `fieldset`s with a
  `legend`; help texts are linked with `aria-describedby`; error summaries are `role="alert"`; messages after saving
  are `role="status"`.
- **Images**: item photos are described by the item's title; thumbnails next to a title link are decorative
  (`alt=""`).
- **Keyboard**: everything works with Tab, Enter and Space, with Bootstrap's visible focus ring. Confirmation dialogs
  are the browser's own.
- **Colour**: Bootstrap 5 colours, which meet AA contrast; status is never shown by colour alone (badges carry text).

`tests/Commonage.BrowserTests` repeats the automated check with axe on every main page, at phone and desktop width, on
every pull request.

## Before each release

Automated checks cannot judge these, so check them by hand on the pages that changed:

- Zoom to 200% and 400%: nothing is cut off, and only long content scrolls sideways.
- Set a large font size in the browser's settings: the text grows with it.
- Go through a task with the keyboard alone: the focus is always visible and in a sensible order.
- Listen to a task with a screen reader (VoiceOver, NVDA or TalkBack), if one is at hand.

## Known gaps

- **Photos have no description of their own.** The item title stands in for every photo of an item. Curators
  should be able to describe each photo (alt text), and contributors could suggest one.
- **Field errors are not linked to their field** with `aria-describedby`; screen readers announce `aria-invalid`
  and the error summary, but not the specific message when the field gets focus.
- **No right-to-left layout** yet (see "Languages" in the roadmap).
- **Not yet tested with real assistive technology** (NVDA, VoiceOver, TalkBack). This belongs in the five-task test
  or a separate session with a screen reader user.
