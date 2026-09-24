# Localisation

i18next with react-i18next. One language ships (English); the scaffolding
exists so that every new string is written translatable from the start,
rather than extracted later, under deadline, from a system in daily use.

## What is translated today

The **shell**: navigation, breadcrumbs, page titles, the skip link, the
session-expiry warning, the sign-in and not-permitted screens, and the
loading / not-found / not-built pages. The screens themselves (records,
review queues, devices, dashboard…) still carry English literals and are
converted screen by screen, using the conventions below.

## Adding a string

1. Add the key to `locales/en/common.json`. English is the source of truth for
   the key set.
2. Use it: `const { t } = useTranslation()` then `t('section.key')`.

Keys are **type-checked** (`i18next.d.ts`): a misspelt or missing key fails
`tsc`, instead of showing a registrar the raw key. Template keys work too —
`` t(`nav.items.${item.id}`) `` — as long as the id is typed to the keys that
exist (see `NavItemId` in `shell/navigation.ts`).

Conventions:

- **Group by where the words appear**, not by component name: `nav.*`,
  `auth.*`, `session.*`. Components get renamed; screens mostly do not.
- **Whole sentences, with placeholders.** `t('auth.needsRoles', { roles })`,
  never `'It needs ' + roles + '.'` — word order differs between languages, and
  a translator cannot reorder a sentence that was built by concatenation.
- **Lists, dates and numbers go through `Intl`** with `i18n.language`
  (`Intl.ListFormat`, `Intl.DateTimeFormat`, `Intl.NumberFormat`) — not
  hand-joined with `', '` and `' or '`.
- **Legal and clinical terms need care, not just translation.** A BRN, an
  ICD code, a statutory term: agree the wording with the Ministry before it
  ships in another language, because the English is what the law names.

## Adding a language

1. Copy `locales/en/common.json` to `locales/<code>/common.json` and translate
   the values (keep the keys).
2. Add it to `resources` in `index.ts`.
3. Decide how the language is chosen (user setting, browser, facility) — not
   decided yet, because only one language exists to choose.

**Right to left.** `<html dir>` follows the language (`i18n.dir()`), so Arabic
flips the document direction without a component changing. What still needs
checking for an RTL language is layout written with physical directions —
`ml-*`, `pl-*`, `left-*` — which should become logical ones (`ms-*`, `ps-*`,
`start-*`) as screens are converted.
