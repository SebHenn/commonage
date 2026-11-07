# Translating Commonage

Commonage is meant to be used in any language. The interface is in English and German so far. This page is for translators,
who need no Git, and for whoever sets up the translation service.

## For translators

Translations are made in [Weblate](https://weblate.org/), in the browser. You see each English text and type yours below it.

- **Write what a person in your language would say,** not word for word. Short sentences in plain language are best, because
  many people in community groups are not used to computers.
- **Keep `{0}`, `{1}` and so on.** They are filled in when the page is shown, for example with a name, a date or a number. You
  may move them within the sentence.
- **Counts have a form for each number.** A text such as `{0, plural, one {# day} other {# days}}` says "1 day" or "30 days".
  Write each form your language needs. `#` is the number. Weblate shows which forms there are, and warns when one is missing. In
  Polish, for example: `{0, plural, one {# zdjęcie} few {# zdjęcia} many {# zdjęć} other {# zdjęcia}}`. Always keep `other`.
- **Some texts need a second translator.** What a contributor agrees to and the privacy text are marked "Please have a second
  translator review it". The agreement is stored with what someone sends in, in the words they saw, so it has to be right the first
  time.
- **Untranslated text is shown in English.** A language appears in the interface once all of it is translated, but every text
  you translate is already useful.
- **Names of files and fields stay English**, such as `archive.json` or `"rotation"` in the text that goes with an export.

Before your first translation, Weblate asks you to accept the project's agreement: your translations are published under the
[GNU Affero General Public License v3.0](../LICENSE), like the rest of Commonage, and you certify the
[Developer Certificate of Origin](https://developercertificate.org/) for them. Weblate records your name and email address as the
author of each change in the public history. If you do not want your email address there, set a private commit email in your
Weblate profile.

## How it is set up

**Decision: Hosted Weblate, on its gratis plan for libre projects.** It runs on [hosted.weblate.org](https://hosted.weblate.org/),
so a volunteer project needs no server for it. The gratis plan is for public libre projects and has the limits of the
160,000-string plan. Commonage has about 600 texts, so even 50 languages stay far below that. It is not set up yet: the maintainer
applies for it.

The settings for the component:

| Setting | Value |
|---|---|
| Version control system | GitHub pull request, from Weblate's fork, to `main` of `SebHenn/commonage` |
| File format | .NET resource file (RESX) |
| File mask | `src/Commonage.Web/Resources/SharedResource.*.resx` |
| Monolingual base language file | `src/Commonage.Web/Resources/SharedResource.resx` |
| Translation flags | `icu-message-format` |
| Contributor license agreement | The paragraph above about the license and the certificate |
| Commit message | Weblate's standard message, followed by an empty line and `Signed-off-by: {{ author }}` |
| Add-ons | "Remove blank strings" and "Cleanup translation files". Not "Update RESX files" or "Prefill translation with source", which fill untranslated texts with English and so hide what is missing. |
| Project | "Enable reviews" on, so that reviewers approve texts |

**Sign-off.** Every commit needs a `Signed-off-by:` line, and the `dco` check refuses a pull request without one. `{{ author }}` is
the translator, who accepted the certificate in the agreement before translating. After the first pull request from Weblate, check
that the line names a person and an address, for example `Signed-off-by: Ana Novak <ana@example.org>`. Weblate's documentation does
not say exactly how it writes the author.

**Checks.** Weblate's pull requests run the same checks as everyone else's. `TranslationTests` refuses a translation that loses a
`{0}`, lacks a form of a count, or is empty, and a translation of a text the code no longer uses. "Remove blank strings" keeps
Weblate from writing empty translations, which would show nothing at all. Someone with write access merges the pull request after
the checks pass.

**Base file.** English is the key in the code, so `SharedResource.resx` is written from the code by `TranslationTests` and is not
built into the application. When text changes, a test fails until the base file is written again, see
[development.md](development.md#translations). Weblate picks up the change after the merge.

## A new language

1. In Weblate, "Start new translation". It adds `SharedResource.<code>.resx` in a pull request.
2. When all of it is translated and reviewed, a developer adds the code to `SupportedLanguages.cs`. From then on a missing
   translation in that language fails the tests.
3. If the language counts in a way `Plurals.cs` does not know yet, add its rules from the
   [CLDR plural rules](https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html), with a test. A language
   that is not listed counts like English: one, and other.
4. For dates, month names already come from .NET in every language. Words such as "circa" and "before" need a `DateVocabulary`
   of their own, with tests in `DateLanguageTests`. A right-to-left language also needs the checks in
   [accessibility.md](accessibility.md).

## Counts: why ICU MessageFormat in RESX

Weblate's RESX format has no plurals. These were the choices:

- **ICU MessageFormat in a RESX value (chosen).** One text holds all forms. Weblate checks the syntax with the `icu-message-format`
  flag, and the application keeps ASP.NET's own localization. No new dependency. `Plurals.cs` reads the part of ICU that counts
  need (`plural`, `=0`, `#`) and holds the CLDR rules for whole numbers. Translators see a little syntax, which Weblate highlights.
- **gettext PO files.** Weblate supports their plurals best. But ASP.NET needs a package to read them, every page would change how
  it looks up text, and the German translation would have to be converted.
- **JSON formats such as ARB.** They also use ICU, but need a reader of our own for ASP.NET, and give nothing beyond the first choice.

In the code, a count in a sentence is `T.Plural("You can send at most {0, plural, one {# photo} other {# photos}} at once.", count)`.
Label form, `Photos: {0}`, needs no plural at all and stays fine where it reads well.
