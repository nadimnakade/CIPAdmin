# Rolling out the Analyzer design language to the whole app

**Scope:** every screen outside `BsaAnalyzerPage` — 17 pages, 8 shared components, 1,263 `className` attributes.
**Stack:** React 18 + Vite + hand-written CSS (no Tailwind, no CSS-in-JS). Tokens are plain CSS custom properties.
**Status of the scaffolding:** `src/styles/tokens.css` and `tools/check-contrast.mjs` already exist and are wired into `npm run build`. **No screen has been changed yet** — `tokens.css` is deliberately not imported, so production renders exactly as before.

---

## 0. Read this first — the Analyzer is not a design system yet

Three measured findings change the shape of this task.

### 0.1 There are two competing design languages, not one

`styles.css` opens with a self-described contract called **"The Registrar's Desk"** — warm oatmeal ledger paper, Source Serif 4 display headings, "rubber-stamp red", a `--stamp: #b4531f` accent, and `border-radius: 0 0 8px 8px` notched card tops. The Analyzer is a **completely different system**: cool white/blue enterprise, Inter throughout, `#195fe0` accent, plain `border-radius: 9px` cards.

| | Legacy (`styles.css`) | Analyzer (`BsaAnalyzerPage.css`) |
|---|---|---|
| Display font | `--font-display: 'Source Serif 4'` serif | Inter only (serif explicitly overridden) |
| Accent | `--stamp: #b4531f` burnt orange | `--bsa-blue: #195fe0` |
| Primary button | burnt orange gradient | blue gradient |
| Card signature | `border-top: 2px solid var(--ink)` notch | flat 1px hairline, 9px radius |
| Theme mechanism | ~40 `:root` variables | 4 ad-hoc `--bsa-*` vars on a page wrapper |

The Analyzer is not an evolution of the global system — it is a **second system that overrides the first on one page only**. `[BsaAnalyzerPage.css:14](CIPAdmin/Frontend/src/pages/BsaAnalyzerPage.css#L14)` explicitly re-asserts `font-family: var(--font)` to cancel the serif, and nearly every rule is double-scoped as `.bsa-workspace .btn` to beat `styles.css` specificity.

**Consequence:** "copy the Analyzer across the app" would mean importing *both* systems and letting specificity fight per-page — unmaintainable. The Analyzer must first be **de-scoped into tokens**, then the tokens replace `:root`. That is Phase 1 below.

### 0.2 The Analyzer's own palette fails WCAG AA in 12 of 18 measured pairs

Measured with the WCAG relative-luminance formula:

| Element | fg / bg | size | Ratio | AA |
|---|---|---|---|---|
| table `th` | `#627391` / `#f2f5fa` | 8px | **4.39** | ✗ |
| card label | `#71809b` / `#fbfcff` | 10px | **3.89** | ✗ |
| card sub | `#7c89a1` / `#fbfcff` | 9px | **3.44** | ✗ |
| table footer | `#7a89a0` / `#fff` | 9px | **3.55** | ✗ |
| detail `dt` | `#8290a6` / `#f5f8fd` | 9px | **3.04** | ✗ |
| drop hint | `#7a87a0` / `#f8fafd` | 10px | **3.46** | ✗ |
| classification note | `#74839b` / `#fff` | 10px | **3.84** | ✗ |
| legend / donut centre | `#7d89a1` / `#fff` | 9px | **3.52** | ✗ |
| tab red / amber | `#dc3c56` / `#b27c13` | 10px | 3.97 / 3.51 | ✗ |
| badge red / amber | `#dc3e57` / `#a77710` | 9px | 3.83 / 3.65 | ✗ |
| table `td` | `#42516a` / `#fff` | 10px | 8.03 | ✓ |
| tab green, badge green, legend text | — | — | 5.19 / 4.64 / 6.00 | ✓ |

The single biggest cause is the `--bsa-muted` family (`#71809b`, `#7c89a1`, `#7a89a0`, `#8290a6`, `#74839b`, `#7a87a0`, `#6c7d96`) — eight near-duplicate greys, all landing in the 3.0–4.0 band. They exist because the Analyzer needed progressively lighter text in a shrinking layout and darkened ad-hoc each time.

### 0.3 The focus ring is effectively invisible — and so is most of the type

- **Focus ring `#accaff` on white = 1.66:1.** WCAG 2.1 SC 1.4.11 requires **3:1** for focus indicators. The Analyzer's ring (`outline: 3px solid #accaff`) passes on a blue-filled button and fails on every white surface. Keyboard users cannot see where they are.
- **Type floor is 8px.** `th` is 8px, `td` 10px, most meta 9px. The skill's typography rule and WCAG both treat sub-12px body text as unreadable; this is legible only on a 1512px desktop at 100% zoom, and unusable at 125–150% Windows scaling, which most corporate desktops run.

**Recommendation: do not roll the Analyzer out verbatim.** Adopt its *structure* — workspace shell, section headings, hairline cards, tone-tinted icon tiles, sortable register table, sticky table header, expandable detail rows — but take colours and type from the corrected token layer. That layer is already written and machine-verified (Phase 1).

---

## 1. Design system documentation

Canonical source: [`tokens.css`](../Frontend/src/styles/tokens.css). 29 contrast assertions pass; re-run `npm run check:contrast`.

### Typography

| Token | Value | Use |
|---|---|---|
| `--font-sans` | `Inter, 'Segoe UI', system-ui, sans-serif` | everything |
| `--font-mono` | `'Cascadia Mono', Consolas, monospace` | IDs, amounts, refs |
| `--fs-xl` | 24px / `--fw-heavy` / `-0.035em` | page h1 |
| `--fs-lg` | 19px | empty-state h2 |
| `--fs-md` | 16px | modal title |
| `--fs-base` | 14px | **app body default** |
| `--fs-sm` | 13px | dense body |
| `--fs-xs` | 12px | **table body — hard floor** |
| `--fs-2xs` | 11px | meta only, 4.5:1 enforced |

Line heights 1.25 / 1.4 / 1.55 / 1.75. Weights 400 / 500 / 600 / 700 / 750.

> Note the deliberate change: the Analyzer used serif for nothing and 8–10px everywhere. `Source Serif 4` is dropped — a serif display face on 12px uppercase table headers is unreadable, and it is the single clearest "Registrar" artefact.

### Colour

| Group | Tokens |
|---|---|
| Ink | `--c-ink #15294f` (14.4:1), `--c-text #35476a` (9.3:1), `--c-text-sub #5b6b85` (5.4:1) |
| Surface | `--c-bg #f4f6fa`, `--c-surface #fff`, `--c-surface-alt #f8fafd`, `--c-surface-sunk #f2f5fa` |
| Line | `--c-line #e3eaf3`, `--c-line-soft #eef2f7`, `--c-line-strong #d7e1ef` |
| Accent | `--c-accent #195fe0`, `--c-action #2563eb`, `--c-accent-deep #1f3a5f`, `--c-accent-soft #edf3ff` |
| Tones | green `5.86`, red `5.49`, blue `6.25`, amber `6.23`, purple `7.33`, neutral `6.12` — each as `--tone-X-fg` on `--tone-X-bg` |
| Focus | `--focus-ring-color #1d4ed8` — **6.70:1**, replaces the 1.66:1 ring |

`--c-text-sub #5b6b85` is the single replacement for all eight failing greys. `--tone-amber-fg` moved from `#b27c13` to `#7a5406` — the amber badge was 3.65:1 and unreadable at 9px.

### Spacing, radius, elevation, motion

4px base scale `--sp-1…10` (4/6/8/10/12/14/18/20/24/32). Radii `--r-xs 4` (badges) → `--r-sm 6` (inputs) → `--r-md 8` (buttons, dropzones) → `--r-lg 10` (cards) → `--r-xl 14` (page shell). Elevation `--sh-xs/sm/md/lg`; page shell only, panels stay flat. Motion `--dur-fast 150ms` / `--dur-base 200ms` / `--dur-slow 300ms` on `--ease-out`, collapsed to 0 under `prefers-reduced-motion`.

---

## 2. Component inventory

Counts are live `className` occurrences across `src/pages` + `src/components`.

| # | Component | Legacy class | Uses | Gap to close | Priority |
|---|---|---|---|---|---|
| 1 | Buttons | `.btn` (+`.primary`,`.secondary`,`.subtle`) | **142** | primary is burnt-orange; padding 10/16 vs Analyzer 9/12; no `:focus-visible` | P0 |
| 2 | Form fields | `.field`, `.field-label`, `.input-wrap` | **159 + 2** | 11px labels, no inline error state | P0 |
| 3 | Cards | `.card` | **215** | `border-top: 2px` notch + serif `h3` conflict with Analyzer | P0 |
| 4 | Panels | `.panel` | **29** | ⚠️ **no `.panel` rule exists in `styles.css`** — 29 usages render unstyled except inside the Analyzer | P0 |
| 5 | Modals | `.modal`, `.modal-head` | **43** | 16px serif title, no focus trap, no `prefers-reduced-motion` | P1 |
| 6 | Alerts / banners | `.alert` | **42** | flat borderless; Analyzer has the rounded 8px tone banner | P1 |
| 7 | Data tables | `.data-table` | **21** | see §4 — the biggest single visual gap | P0 |
| 8 | Icon buttons | `.icon-btn` | **43** | no `aria-label` enforcement, 32px hit target (< 44px) | P1 |
| 9 | Badges / status | `.badge` (+ 6 variants) | **17** | `border-radius: 999px` pill vs Analyzer's 4px chip; 3 duplicated `.badge.blue` rules | P1 |
| 10 | Stat cards | `.stat-card`, `.stat-icon` | **10** | `class="card stat-card"` double-class idiom; needs the Analyzer tile+figure+sub layout | P1 |
| 11 | Search / filter | `.search-box`, `.filter-select` | **12** | no clear button, no result count | P2 |
| 12 | Dropzones | `.dropzone` | **1** | Analyzer version is the reference | P3 |
| 13 | Tabs / subtabs | `.subtabs`, `.subtab` | — | underline tabs vs Analyzer tone pills | P2 |
| 14 | Toasts / confirms | `.toast-*`, `.confirm-backdrop` | — | already built to Analyzer language ✅ | — |
| 15 | Progress | `.top-progress` | — | already correct ✅ | — |

Also worth folding in: `.card { overflow-x: auto }` at [styles.css:1358](CIPAdmin/Frontend/src/styles.css#L1358) makes *every* card a scroll container, and `.badge.blue` is declared three times (lines 928, 1020, 2013) with a fourth variant at 1788.

---

## 3. Migration strategy

Seven phases, each independently shippable. **Phase 1 is already done.**

### Phase 1 — Token layer + enforcement gate ✅ complete
`tokens.css` written; `tools/check-contrast.mjs` added; `npm run build` now runs the gate. Verified: `tsc -b` exit 0, `vite build` exit 0, gate 29/29 pass. Not yet imported, so zero visual risk.

### Phase 2 — Cut the seam ✅ complete
Shipped as a pure token remap — no component markup changed.
1. `tokens.css` imported in `main.tsx`. Import order is **load-bearing** and now commented in the file: page-scoped CSS is pulled in transitively by `App` and lands *first* (verified at byte offset 0 of the built bundle), then `styles.css`, then `tokens.css` last. Loading the token layer last is what lets its canonical focus ring win over `styles.css`'s legacy `:focus-visible` rule.
2. `:root` in `styles.css` rewritten as **aliases** onto tokens — `--primary`, `--ink`, `--bg`, `--card`, `--border`, `--muted`, `--radius`, `--shadow` all now resolve through the token layer, and `--stamp`/`--stamp-dark` map to `--c-action`/`--c-action-hover`, retiring burnt orange app-wide.
3. All **6** `--font-display` usages deleted (`--font-display` itself removed from `:root`). `font-weight: 600` added to `.card-head h3` and `.confirm-title` to preserve the hierarchy the serif was carrying.
4. Status colours re-based for AA: `--red` `#b3341f`→`#b32a44`, `--amber` `#a16207`→`#7a5406`, `--green` `#2e5d3a`→`#0f6b3d`, `--info` `#1f6f8b`→`#1257a8`, each paired with its verified `-light` tint.
5. `.btn.warn` decoupled from the retired stamp (now `#7a5406` on `#fdf3e4`, 6.17:1), and the **duplicate `.badge.orange`** declaration collapsed to one.

**Three accessibility bugs found and fixed while doing it** — all were invisible, not cosmetic:
| Bug | Was | Now | Measured in DOM |
|---|---|---|---|
| Analyzer focus ring (3 rules) | `#accaff` | `var(--focus-ring-color)` | 1.66:1 → **6.70:1** |
| App-wide wrapped-input ring | `rgba(37,99,235,.16)` | solid `var(--focus-ring-color)` | **1.25:1** → 6.70:1 |
| "Filter is active" border | `#accaff` | `var(--c-accent)` | 1.66:1 → **5.59:1** |

The 1.25:1 input ring was the worst of the three: `.input-wrap`/`.field` suppress the native outline and draw this ring instead, so *every wrapped input in the app* — including both login fields — had effectively no visible focus indicator.

> **Deliberately unchanged:** `.card`'s 2px notch, `.data-table`'s transparent header, `.badge`'s 999px pill. Those are component structure, i.e. Phase 3. Phase 2 changed colour and type only, so each page's silhouette is untouched and the diff bisects cleanly.

### Phase 3 — Shared primitives (highest leverage, 3 files)
Rebuild `.btn`, `.card`, `.data-table` against tokens. These three classes cover **142 + 215 + 21 = 378** usages and retro-fit every page at once. Delete `.bsa-workspace .btn` etc. as each lands so there is never a scoped override to maintain.

### Phase 4 — Forms + feedback
`.field` / `.alert` / `.modal` (244 uses). Add visible labels, inline `aria-describedby` errors, `role="alert"` on banners, focus trap + `Escape` on modals.

### Phase 5 — Page-by-page
Strict order by dependency and risk: `LoginPage` → `DashboardPage` → `DialerLivePage` → the master CRUD screens (`EmployeesPage` 88, `EmployeeMasterPage` 72, `RoleMasterPage` 43, `CompanyCategoryPage` 40, `LeadMasterPage` 35) → `CibilCheckerPage` → `OcrPage` → `SettingsPage`/`AccessControlPage`/`GenericReportsPage` → `FinancialAnalysisPage`.

> `FinancialAnalysisPage.tsx` is the one page flagged in earlier work as still on the pre-redesign layout. Migrate it in the same PR as `BsaAnalyzerPage` so the two sibling screens ship together.

### Phase 6 — Retire the Analyzer skin
Once tokens cover everything, strip the `.bsa-workspace` prefix, unwrap the 4 `--bsa-*` vars onto tokens, and delete `BsaAnalyzerPage.css`'s duplicated declarations. Target: the file shrinks to page-specific layout only.

### Handling legacy styles
- **Never** raise specificity to out-vote a legacy rule. Add the token-based rule, verify the screenshot, delete the legacy rule in the same commit.
- Delete, do not comment out.
- One component class per PR, so any regression bisects to a single file.
- Search-and-replace `.badge.blue` duplicates as part of Phase 3 — four rules for one class is where drift starts.

---

## 4. Code examples

### 4.1 Card — `.stat-card`

**Before** — [styles.css:563](CIPAdmin/Frontend/src/styles.css#L563) + [styles.css:594](CIPAdmin/Frontend/src/styles.css#L594). Notched top border, serif heading, 46px icon tile, 20px padding:

```css
/* styles.css — legacy */
.card {
  background: var(--card);
  border: 1px solid var(--border);
  border-top: 2px solid var(--ink);     /* file-cover notch */
  border-radius: 0 0 var(--radius) var(--radius);
  box-shadow: var(--shadow);
  padding: 20px;
}
.card-head h3 {
  margin: 0; font-size: 15px;
  font-family: var(--font-display);      /* Source Serif 4 */
  color: var(--ink);
}
.stat-card { display: flex; gap: 14px; align-items: center; }
.stat-icon {
  width: 46px; height: 46px; border-radius: 12px;
  display: flex; align-items: center; justify-content: center;
  flex-shrink: 0;
}
```

```tsx
// DashboardPage.tsx:141 — current markup
<div className="card stat-card" key={c.label}>
  <div className={`stat-icon tone-${c.tone}`}><Icon name={c.icon} /></div>
  <div>
    <div className="muted-note">{c.label}</div>
    <strong>{c.value}</strong>
  </div>
</div>
```

**After** — token-based, flat hairline, gradient wash, `--sp-*` rhythm. `tone-*` comes from the verified pair set so the label is guaranteed readable:

```css
/* tokens.css — semantic tone utilities (already AA-verified) */
.tone-green  { background: var(--tone-green-bg);  color: var(--tone-green-fg);  }
.tone-red    { background: var(--tone-red-bg);    color: var(--tone-red-fg);    }
.tone-blue   { background: var(--tone-blue-bg);   color: var(--tone-blue-fg);   }
.tone-amber  { background: var(--tone-amber-bg);  color: var(--tone-amber-fg);  }
.tone-neutral{ background: var(--tone-neutral-bg);color: var(--tone-neutral-fg);}

.card {
  background: linear-gradient(150deg, var(--c-surface), var(--c-surface-alt));
  border: 1px solid var(--c-line);
  border-radius: var(--r-lg);          /* 10px, no notch */
  box-shadow: var(--sh-md);
  padding: var(--sp-9);                /* 24px */
}
.card-head h3 {
  margin: 0;
  font-size: var(--fs-md);
  font-weight: var(--fw-semi);
  color: var(--c-ink);
  letter-spacing: var(--ls-snug);
}

/* figure + label + optional sub, matching the Analyzer tile */
.stat-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--sp-4);
  padding: 15px var(--sp-5);
  min-width: 0;                        /* let grid children shrink */
}
.stat-card__icon {
  width: 37px; height: 38px;
  border-radius: var(--r-md);
  display: grid; place-items: center;
  flex-shrink: 0;
}
.stat-card__label {
  font-size: var(--fs-2xs);             /* 11px meta — AA enforced */
  line-height: var(--lh-snug);
  color: var(--c-text-sub);            /* 5.40:1, was 3.89:1 */
}
.stat-card__value {
  font-size: var(--fs-2xl);
  font-weight: var(--fw-bold);
  letter-spacing: var(--ls-tight);
  line-height: var(--lh-tight);
  font-variant-numeric: tabular-nums;
}
.stat-card__sub {
  font-size: var(--fs-2xs);
  color: var(--c-text-sub);
  line-height: var(--lh-snug);
}
.stat-card.clickable { cursor: pointer; transition: box-shadow var(--dur-fast) var(--ease-out); }
.stat-card.clickable:hover { box-shadow: var(--sh-lg); transform: translateY(-1px); }
@media (prefers-reduced-motion: reduce) {
  .stat-card.clickable { transition: none; }
  .stat-card.clickable:hover { transform: none; }
}
```

```tsx
<div className="card stat-card" key={c.label}>
  <div className={`stat-card__icon tone-${c.tone}`}><Icon name={c.icon} /></div>
  <div className="stat-card__label">{c.label}</div>
  <div className="stat-card__value">{c.value}</div>
  {c.sub && <div className="stat-card__sub">{c.sub}</div>}
</div>
```

### 4.2 Table — `.data-table`

**Before** — [styles.css:872](CIPAdmin/Frontend/src/styles.css#L872). 12px uppercase header on a 2px ink rule, no surface fill, no sticky header, no numeric alignment discipline, and a `min-width: 640px` override at line 1354 that forces every table to scroll:

```css
/* styles.css — legacy */
.data-table { width: 100%; border-collapse: collapse; }
.data-table th {
  text-align: left;
  color: var(--muted);            /* 4.83:1 — passes, but 11px label does not */
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  padding: 10px 12px;
  border-bottom: 1px solid var(--ink);   /* 2px hard rule (line 2257) */
  white-space: nowrap;
}
.data-table td {
  padding: 12px;
  border-bottom: 1px solid var(--border-light);
  font-variant-numeric: tabular-nums;
}
.data-table tr:hover td { background: rgba(31, 58, 95, 0.035); }
.data-table { min-width: 640px; }         /* line 1354 — global, unwanted */
```

**After** — ported from the Analyzer's `.bsa-register-table`, but with the type floor and header colour corrected:

```css
.data-table {
  width: 100%;
  border-collapse: collapse;
  table-layout: auto;
}
.data-table thead th {
  position: sticky; top: 0; z-index: 1;
  background: var(--c-surface-sunk);      /* #f2f5fa — was transparent */
  color: var(--c-ink);                    /* 13.14:1, was #627391 @ 4.39:1 */
  font-size: var(--fs-2xs);
  font-weight: var(--fw-semi);
  text-transform: uppercase;
  letter-spacing: var(--ls-wide);
  line-height: var(--lh-snug);
  padding: var(--sp-4) var(--sp-3);
  text-align: left;
  white-space: nowrap;
  border-bottom: 1px solid var(--c-line);
}
.data-table thead th button {           /* sortable header */
  display: inline-flex; align-items: center; gap: 3px;
  background: none; border: 0; padding: 0;
  font: inherit; letter-spacing: inherit; text-transform: inherit;
  color: inherit; cursor: pointer;
}
.data-table thead th button:focus-visible { outline-offset: 4px; }

.data-table tbody td {
  padding: var(--sp-4) var(--sp-3);
  font-size: var(--fs-xs);               /* 12px floor, was 10px */
  line-height: var(--lh-snug);
  color: var(--c-text);                  /* 9.29:1, was #42516a @ 8.03:1 at 10px */
  border-bottom: 1px solid var(--c-line-soft);
  vertical-align: middle;
}
.data-table tbody tr:nth-child(even) { background: var(--c-surface); }
.data-table tbody tr:hover td { background: var(--c-surface-alt); }

/* amounts: right-aligned, tabular, never wrapping */
.data-table .numeric {
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.data-table th[aria-sort='ascending']::after  { content: ' \25B2'; }
.data-table th[aria-sort='descending']::after { content: ' \25BC'; }

/* long text truncates instead of exploding the row */
.data-table .clamp-2 {
  display: -webkit-box; -webkit-line-clamp: 2;
  -webkit-box-orient: vertical; overflow: hidden;
  overflow-wrap: anywhere;
}

/* scroll lives on the wrapper only — drop min-width: 640px from styles.css */
.table-scroll { overflow: auto; max-height: 530px; scrollbar-width: thin; }

@media (prefers-reduced-motion: reduce) {
  .data-table tbody tr { transition: none; }
}
```

```tsx
<div className="table-scroll">
  <table className="data-table">
    <caption className="sr-only">{caption}</caption>
    <thead>
      <tr>
        <th scope="col" aria-sort={sort.date === 'asc' ? 'ascending' : undefined}>
          <button onClick={() => toggleSort('date')}>Date</button>
        </th>
        <th scope="col" className="numeric">
          <button onClick={() => toggleSort('debit')}>Debit</button>
        </th>
      </tr>
    </thead>
    <tbody>{/* … */}</tbody>
  </table>
</div>
```

`aria-sort` + the pseudo-element arrow replaces the icon swap, so the sort state is announced rather than merely drawn.

---

## 5. Quality assurance

### 5.1 Contrast — automated, already wired
`npm run check:contrast` parses `tokens.css` and asserts 29 pairings (text ≥ 4.5:1, focus indicator ≥ 3:1). It runs as part of `npm run build` and **exits 1 on violation**, so a bad colour cannot reach a build. Extend it to parse component CSS once Phase 3 lands — that is what stops the eight-drift-greys problem recurring.

### 5.2 Visual regression
- Playwright snapshots at **375 / 768 / 1024 / 1440**, covering every page in Phase 5 plus one keyboard-focus state per page.
- `toHaveScreenshot` with `maxDiffPixelRatio: 0.01` — enough tolerance for font rasterisation, tight enough to catch a 1px hairline shift.
- Snapshot the **focus-visible** state explicitly; it is the regression this codebase is most likely to ship blind.
- Run the sweep in CI on PRs that touch `tokens.css` or any `*.css`.

### 5.3 Design linting (no-CSS hardcoding)
Block hex literals and raw px in components so tokens cannot be bypassed:
- `stylelint` `declaration-property-value-allowed-list` / `color-no-hex`.
- ESLint `no-restricted-syntax` banning `style={{ … }}` colour props — all styling stays in CSS against tokens.
- A CI grep guard: `grep -rnE '#[0-9a-fA-F]{3,8}' src/pages src/components --include='*.tsx'` must return nothing.

### 5.4 Accessibility gates
- **Contrast:** the token gate (§5.1) plus axe-core in Playwright (`@axe-core/playwright`), asserting `color-contrast` and `focus-visible` on every route.
- **Focus:** tab through each page; every stop shows the `--focus-ring-color` ring. Explicitly regression-test the 1.66:1 ring that shipped on the Analyzer — if it appears in a diff, the change is wrong.
- **Type:** lint a lower bound of 11px on body/meta and 12px on table content. The 8px header must not return.
- **Touch targets:** `.icon-btn` is 32px today against the 44px minimum — raise hit area with a pseudo-element rather than changing visual size.
- **Motion:** `prefers-reduced-motion` is honoured globally in `tokens.css`; add a Playwright context with `reducedMotion: 'reduce'` asserting the top progress bar and toast transitions resolve to 0ms.
- **Motion budget:** animate 1–2 elements per view. The top progress bar and toast slide are the only global animations; keep it that way.

### 5.5 Definition of done per phase
1. `npm run check:contrast` passes (29/29).
2. `tsc -b --force` and `vite build` exit 0.
3. Screenshots at 4 widths, no horizontal overflow, focus ring visible.
4. axe-core clean on the touched routes.
5. Legacy rules for that component **deleted**, not commented out.

---

## Decisions taken (confirmed)

1. **Serif retired** — `Source Serif 4` dropped, Inter throughout. Confirmed and applied in Phase 2.
2. **Blue is the accent** — `--stamp` orange aliased away to `--c-action`. Confirmed and applied in Phase 2.
3. **Table density** — `--fs-xs` (12px) stands as the accessible floor; to be settled visually in Phase 3 with screenshots.

## Still open

- **Table density** is the one call that needs eyes on it: 12px body is the AA floor but not the densest option. Decide during Phase 3 against real screens.
- **`FinancialAnalysisPage`** is the one screen still on the pre-redesign layout; schedule it alongside `BsaAnalyzerPage` in Phase 5.