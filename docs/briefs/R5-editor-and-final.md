# Brief R5: UI redesign, Editor role, screenshots and final pass

Read AGENTS.md first and follow it, including "How changes flow" (start with
`git pull --ff-only origin blog-framework`) and "Finishing a task".

**What to build:** Part R5 of the implementation plan
`docs/superpowers/plans/2026-10-05-ui-redesign.md`, against the spec
`docs/superpowers/specs/2026-10-04-ui-redesign-design.md`. The plan's tasks are the items: one commit per task,
TDD, the plan's tests (plus any you need), break-it-once checks where the plan names them.

- Use `subagent-driven-development` with the plan file itself and Part R5 (do not rewrite it into a new plan).
  Each implementer subagent gets its task's full text, the plan's "Global Constraints" and "Review Focus"
  sections, and AGENTS.md.
- Component names, folders and parameters are fixed by spec §7 and the plan's interfaces. If something in the
  plan contradicts the spec, the spec wins for behaviour and the plan wins for names; record a ruling.
- Implement Part R5 only. Do not start the next part.

Requires R4 accepted. Finish with the whole E2E suite passing twice in a row.

## Out of scope
Everything outside Part R5; Kubernetes; letting Editors create posts.
