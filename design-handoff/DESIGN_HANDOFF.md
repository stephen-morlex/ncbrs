# Birth Registration app: UI redesign handoff

This folder is the design reference for restyling the mobile app. The `mockups/` folder holds one HTML file per screen. Each one is a static visual reference written for a design canvas, so read them for layout, spacing, copy and colors; do not copy their markup into the app.

## Rules for the implementation

1. **Change the UI only.** Keep all existing behaviour as it is: offline storage, sync, certificate numbers, PIN handling, printing, USB export and the Arabic translation.
2. **Build shared theme tokens first** (see "Design tokens" below), then restyle one screen at a time, in the order listed under "Screens".
3. **Reuse the existing strings and translation keys.** Where the copy changes, update both English and Arabic. Flag any new Arabic string for review by a native speaker.
4. **Mirror every layout in Arabic (RTL).** This includes the back chevrons, the progress bars and any icons that point in a direction.
5. **Make touch targets at least 48dp.** Keep body text at 16sp or larger and helper text at 13sp or larger.
6. **Hide the debug buttons** ("Printer (debug)" and "Home (debug)") outside debug builds.
7. **Remove the internal note** "First version of this form, to be reworked…" from the registration form.

## Design tokens (shadcn preset b1ZOMFeKW, light)

| Token | oklch | Hex (approx.) | Use |
|---|---|---|---|
| background | oklch(1 0 0) | #FFFFFF | Screen background, cards |
| foreground | oklch(0.145 0 0) | #0A0A0A | Main text |
| primary | oklch(0.488 0.243 264.376) | #1447E6 | Primary buttons, active nav, selected chips, focus |
| primary-foreground | oklch(0.97 0.014 254.604) | #EFF6FF | Text on primary; light tint behind icons and avatars |
| primary-strong | oklch(0.424 0.199 265.638) | #193CB8 | Text on the light primary tint (badges) |
| secondary | oklch(0.967 0.001 286.375) | #F4F4F5 | Neutral badges (Draft, Certificate printed) |
| secondary-foreground | oklch(0.21 0.006 285.885) | #18181B | Text on secondary |
| muted | oklch(0.97 0 0) | #F5F5F5 | Tab track, stat boxes, input prefix |
| muted-foreground | oklch(0.556 0 0) | #737373 | Helper text, inactive nav, captions |
| border / input | oklch(0.922 0 0) | #E5E5E5 | Card, input and divider borders |
| ring | oklch(0.708 0 0) | #A1A1A1 | Dashed "Someone else" border, empty PIN dots |
| destructive | oklch(0.577 0.245 27.325) | #E7000B | Errors |
| pending (extra) | n/a | #B26A00 on #FCEFD6, text #8A5300 | "Waiting to sync" state only |

A dark theme exists in the same preset (the `.dark` values). Implement light first, but read every color from the theme so dark mode can be added later.

**Shape and elevation**
- Base radius is 10px (0.625rem). Use it for buttons, inputs and chips. Use 14px for cards and the big "Register a birth" card. Use 6px for badges. The selected tab inside a tab track uses 7px.
- Cards, inputs and outline buttons have a 1px #E5E5E5 border and the shadow `0 1px 2px rgba(0,0,0,0.05)`.
- A focused input has a 1px primary border plus a 3px ring of primary at 20% opacity.

**Type**
- Latin text uses Geist, with Noto Sans Arabic for Arabic. Registration numbers use Geist Mono.
- Screen titles are 26px semibold with letter-spacing -0.025em. Section titles are 22px semibold.
- Field labels are 14px semibold. Body text is 15–16px. Helper text is 13px in muted-foreground.

**Icons**
- Use Lucide-style outline icons at 2px stroke. Every icon-only button needs an accessible label.

## Spacing system (apply everywhere, no one-off values)

Use only these values: 4, 8, 12, 16 and 20px. Define them once as spacing tokens and never hard-code other numbers.

| Where | Value |
|---|---|
| Screen edge padding (left and right) | 20px |
| Screen top padding | 20px |
| Gap between sections on a screen | 20px |
| Section label (e.g. "Recent births", "Today") to its content | 8px |
| Field label to its input | 8px |
| Between chips in a row, between tabs and the input under them | 8px |
| Between items in a grid (task tiles, stats, keypad keys, footer buttons) | 12px |
| Between search field and filter chips | 12px |
| Icon to text inside a row or card | 12px |
| Card inner padding | 16px |
| List row padding inside a card | 12px top and bottom, 16px left and right |
| Input inner padding | 14px left and right |
| Badge padding | 4px top and bottom, 8px left and right |
| Sticky footer padding | 16px top, 20px sides and bottom |
| Title to subtitle | 4px |
| Bottom padding of scroll content above a sticky footer or nav | enough to clear it (96px for nav, 120px for footer) |

**Cards are all built the same way:** white background, 1px #E5E5E5 border, 14px radius, the faint shadow, 16px padding. A card that holds a list has no outer padding; each row has 12px/16px padding and a 1px #E5E5E5 divider between rows.

**Do not use margins to fix spacing.** Lay out each screen as a vertical stack with a 20px gap, and group a label with its content in an inner stack with an 8px gap.

## Shared components

- **Primary button:** 56px tall, primary background, primary-foreground text, 10px radius, full width in footers.
- **Outline button:** white background with a 1px border and the faint shadow. It uses a primary border and text when it is the secondary action on a screen.
- **Choice chip:** 44px tall with a 10px radius. Unselected chips use the outline style. Selected chips fill with primary.
- **Tabs (segmented control):** a #F5F5F5 track with 3px padding. The selected tab is white with a shadow; unselected tabs show muted-foreground text. Use this for questions with two or three exclusive answers.
- **Text input:** 52px tall with the label above the field, never underline-only. Units sit inside the field on the right ("g", "weeks", "years"). A prefix like "+211" sits in a muted cell on the left.
- **Badge:** 12px semibold with a 6px radius. "Registered" uses the light primary tint with primary-strong text. "Draft" and "Certificate printed" use secondary. "Waiting to sync" uses pending.
- **Step header:** a back button, the title "Register a birth" and a "Draft saved" indicator, then "Step N of 5 · Name" and a 5-segment progress bar (6px tall).
- **Sticky footer:** white with a top border. It holds either one primary button, or Back (outline, one third of the width) and Next (primary, two thirds).
- **Bottom navigation:** 4 items (Home, Register, Records, More), each with an icon and a text label. The active item is primary with a light-tint pill behind its icon. This replaces the 5 unlabeled icons and the separate status strip above them.

## Screens

1. **Unlock** (`1-unlock.html`)
   - A brand row with the language button.
   - The heading "Welcome back" and the facility name.
   - Staff cards for choosing who is using the tablet (selected card has a primary border, a check and initials), plus a "Someone else" card.
   - Four PIN dots and a 3×4 keypad with "Forgot PIN?" and backspace.
   - "Check a certificate" as an outline button, and "Set or change my PIN · needs signal" as a link.
2. **Home** (`2-home.html`)
   - The greeting, facility, language button and avatar.
   - One sync card with a Sync button and two stats: waiting to sync and certificate numbers left. This replaces both the duplicated stat cards and the status strip.
   - The large primary "Register a birth" card.
   - A 2×2 grid of other tasks: Check a certificate, Print a certificate, Lock the tablet, Export to USB.
   - Recent births with badges and a "See all" link.
3. **Records** (`3-records.html`, new screen)
   - A search field and filter chips: All, Drafts, Waiting to sync, Registered.
   - A list grouped by day. A draft shows "Continue" and reopens the form at the step it reached.
4. **Register: the child** (`4-register-child.html`, step 1 of 5)
   - Given names and surname. The surname placeholder reads "Usually the father's name".
   - Date of birth with "Today" and "Yesterday" chips plus a date field.
   - Place of birth as chips.
   - Sex and number of babies as tabs.
   - Optional weight and pregnancy length.
5. **Register: the mother** (`5-register-mother.html`, step 2 of 5)
   - Full name, and age with an "I know her birth date" option.
   - Nationality as chips. ID document as tabs, with an ID number field.
   - An optional phone number with the +211 prefix.
   - A collapsible "Maternal statistics (optional)" section, which replaces the old checkbox.
6. **Steps 3 and 4: the father, and the parents' marriage and proof of address.** There are no mockups for these. Build them from the same components, following step 2's pattern. Then add step 5, a review screen that summarises the answers, with an "Edit" link on each section.
7. **Birth registered** (`6-registered.html`)
   - A check icon, the registration number in mono, and the note "Write this on the mother's card".
   - A summary card.
   - A three-stage tracker: saved on this tablet, reaches the registry, certificate ready to print.
   - "Sync now" as the primary button and "Register another birth" as the outline button.

## Behaviour notes implied by the design

- **Autosave drafts.** Save the registration form after every step and show "Draft saved". Drafts appear under Records.
- **Splitting the form into steps must not change validation.** Validate each step's required fields before moving to the next one. Validate everything again on the review step.
- **The status card's sync button triggers the existing sync logic.** It should show these states:
  - synced
  - waiting (with a count)
  - offline
  - low on certificate numbers
