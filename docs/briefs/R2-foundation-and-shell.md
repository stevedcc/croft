# Brief R2: UI redesign, Visual foundation and layout shell

Read AGENTS.md first and follow it, including "How changes flow" (start with
`git pull --ff-only origin blog-framework`) and "Finishing a task".

**What to build:** Part R2 of the implementation plan
`docs/superpowers/plans/2026-10-05-ui-redesign.md`, against the spec
`docs/superpowers/specs/2026-10-04-ui-redesign-design.md`. The plan's tasks are the items: one commit per task,
TDD, the plan's tests (plus any you need), break-it-once checks where the plan names them.

- Use `subagent-driven-development` with the plan file itself and Part R2 (do not rewrite it into a new plan).
  Each implementer subagent gets its task's full text, the plan's "Global Constraints" and "Review Focus"
  sections, and AGENTS.md.
- Component names, folders and parameters are fixed by spec §7 and the plan's interfaces. If something in the
  plan contradicts the spec, the spec wins for behaviour and the plan wins for names; record a ruling.
- Implement Part R2 only. Do not start the next part.

Requires R1 accepted. The fonts must be the official OFL releases (Adobe Source Serif 4 / Source Sans 3, JetBrains Mono) downloaded inside your sandbox and committed with their licence files. Copy the logo, tartan and icon files from `docs/superpowers/specs/2026-10-04-ui-redesign/`. The no-JavaScript layout tests are the point of this part: if Playwright can't measure layout with JS disabled by either method in the plan, stop and report.

## Out of scope
Everything outside Part R2; Kubernetes; letting Editors create posts.
