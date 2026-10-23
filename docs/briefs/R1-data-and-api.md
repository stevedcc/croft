# Brief R1: UI redesign, Data model and API

Read AGENTS.md first and follow it, including "How changes flow" (start with
`git pull --ff-only origin blog-framework`) and "Finishing a task".

**What to build:** Part R1 of the implementation plan
`docs/superpowers/plans/2026-10-05-ui-redesign.md`, against the spec
`docs/superpowers/specs/2026-10-04-ui-redesign-design.md`. The plan's tasks are the items: one commit per task,
TDD, the plan's tests (plus any you need), break-it-once checks where the plan names them.

- Use `subagent-driven-development` with the plan file itself and Part R1 (do not rewrite it into a new plan).
  Each implementer subagent gets its task's full text, the plan's "Global Constraints" and "Review Focus"
  sections, and AGENTS.md.
- Component names, folders and parameters are fixed by spec §7 and the plan's interfaces. If something in the
  plan contradicts the spec, the spec wins for behaviour and the plan wins for names; record a ruling.
- Implement Part R1 only. Do not start the next part.

Backend only. Generate the EF migration AFTER pulling (brief 04 added a migration; yours must come after it). Keep Blog.Web compiling with minimal mechanical changes only (show `post.Category?.Name` where the list was shown); the real UI work is later parts.

## Out of scope
Everything outside Part R1; Kubernetes; letting Editors create posts.
