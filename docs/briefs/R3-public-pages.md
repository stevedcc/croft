# Brief R3: UI redesign, Public pages

Read AGENTS.md first and follow it, including "How changes flow" (start with
`git pull --ff-only origin blog-framework`) and "Finishing a task".

**What to build:** Part R3 of the implementation plan
`docs/superpowers/plans/2026-10-05-ui-redesign.md`, against the spec
`docs/superpowers/specs/2026-10-04-ui-redesign-design.md`. The plan's tasks are the items: one commit per task,
TDD, the plan's tests (plus any you need), break-it-once checks where the plan names them.

- Use `subagent-driven-development` with the plan file itself and Part R3 (do not rewrite it into a new plan).
  Each implementer subagent gets its task's full text, the plan's "Global Constraints" and "Review Focus"
  sections, and AGENTS.md.
- Component names, folders and parameters are fixed by spec §7 and the plan's interfaces. If something in the
  plan contradicts the spec, the spec wins for behaviour and the plan wins for names; record a ruling.
- Implement Part R3 only. Do not start the next part.

Requires R2 accepted. Keep brief 04's page-view beacon working on the rebuilt post page (its E2E test must still pass). Compare your pages against the mockup screenshots in `docs/superpowers/specs/2026-10-04-ui-redesign/mockups/*.png` (post-cards option D, post-page-v2 option 2, no-banner-v2 option 2, green-accent option B).

## Do first: shell fixes found in the orchestrator's visual check of R2

Write each as a failing test first (E2E, computed styles), then fix. One commit.

1. **Sidebar styles never reach the link element.** `NavItem` renders through `NavLink`; scoped CSS
   (`NavItem.razor.css`) does not apply to a child component's root element, so `.nav-item`,
   `.nav-item.active` and `.count` rules are dead. Fix with a wrapper element + `::deep` (or move the
   rules to `app.css`). Tests: on `/`, the "Home" item's computed `background-color` equals
   `--primary-strong` (`rgb(114, 5, 52)` light) and its text is white; a category's count is
   right-aligned (count's right edge within 16px of the item's right edge); footer items share the same
   left x.
2. **Dark-mode MudBlazor contrast.** Set `PaletteDark.Primary = "#E88AA6"` and
   `PaletteDark.PrimaryContrastText = "#1E1819"` in `BlogTheme` (orchestrator ruling: readable crimson
   text buttons and readable filled buttons; our own `--primary-strong` still drives the active nav).
   Test: in dark mode a text-variant MudButton's computed colour has contrast ≥ 4.5:1 against `--surface`.
3. **Pin the scoped-CSS link**: one E2E assertion that a scoped style applies (item 1's test covers it).

## Hand-offs from R2's final review
- Use the class names already defined in `app.css`: `.markdown-body`, `.hero-title`, `.subtitle`.
- E2E tests accumulate posts in the shared dev DB; before any list-count assertion, make the test create
  what it counts (unique titles/category) rather than assuming an empty DB. `ThemeTests` should reuse an
  existing post instead of publishing a new one each run.

## Out of scope
Everything outside Part R3; Kubernetes; letting Editors create posts.
