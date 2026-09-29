# The tablet's words, in English and Arabic

`Strings.resx` is English (the neutral culture) and `Strings.ar.resx` is Modern
Standard Arabic. A strongly typed `Strings` class is generated from the English
file at build time, so a missing string is a compile error, not a blank label.

**The Arabic is a draft for the Ministry to review.** Legal and clinical terms
are marked with a comment in `Strings.resx`: late registration, the sworn
affidavit, the provisional slip, and "held, not confirmed". What the law names
is not a wording choice.

## Changing or adding a string

1. Add or change the entry in **both** files, with the same key.
2. Keep the placeholders (`{0}`, `{1:d MMM yyyy}`) the same in both; word order
   may differ.
3. Use it as `Strings.Key`, or `Language.Format(Strings.Key, …)` when it has
   placeholders. Never build a sentence by concatenation, because Arabic orders
   it differently.

`LanguageTests` fails if:
- a key is missing in either language;
- an Arabic value has no Arabic in it (the language switch itself excepted);
- the placeholders differ;
- a coded answer (sex, plurality, evidence type, education level, outcome) has
  no name.

## What stays in English, on purpose

- **The registry's own messages**: a refusal's reasons come from the registry,
  in its language, until the registry is translated.
- **The tablet's copies of the registry's rules** (`RegistrationRules`,
  `PinPolicy`): parity tests hold them to the registry's words exactly. The
  form shows the *field* in the registrar's language beside the rule's words.
- **Reasons written to the registry's audit trail** (a device revocation's
  reason): they are records, read at the centre.

## Numbers and digits

A numeric keypad in Arabic types Arabic-Indic digits (٣١٠٠), and the Arabic
decimal separator ٫. `Language.WesternDigits` reads them as digits, so a birth
weight or a PIN typed in either script is the same number.
