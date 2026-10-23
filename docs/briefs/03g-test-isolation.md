# Brief 03g: E2E tests own their database

Read AGENTS.md first and follow it, including "Finishing a task". Use `subagent-driven-development`
with the items below as the tasks; one commit per item, TDD where there is behaviour, E2E twice in a
row at the end (on the new isolated stack), then the final whole-branch review.

**Why (owner's rule, 2026-10-09):** tests must not depend on the current state of a database. If
they need preloaded data, a script loads it. Today every E2E run, the agents' and the owner's
sandboxed `verify`, uses the one shared dev database `blog` (from `.env`), and the suite never
deletes what it creates: the dev DB holds 3,262 posts, 379 categories, 2,350 tags and 590 media
rows, almost all from test runs. Pages that list everything (tags, sidebar categories, media) slow
down as it grows, and tests fail on timeouts that move between runs. `verify` also runs from a
fresh checkout whose `uploads` folder is empty while the shared DB references 590 files (5,707
"file not found" errors in one run).

**Measured on 2026-10-09** (accepted code `78258e5`, dev DB as it was): **14 of 125 E2E tests
failed, all the same way**: the fixture helper that creates a category on `/admin/tags` waits for its
row ("Icon for R33 Empty …") and times out after 30 s, because that page re-renders ~2,700 editable
rows. `HomeTests` creates its category in its one-time setup, so one timeout failed the whole class
(home, search, category, tag and paging tests). The run took 20 minutes instead of ~11, and the
Web process grew to 2.3 GiB. The owner then deleted the dev data (the `blog` database recreated
empty); the same suite on the empty database is the comparison run recorded in the ledger.

## Item 1: an isolated E2E stack

- `./start-local.sh --e2e [--instance N]` (N defaults to 1) starts one **isolated instance** of the
  stack for the E2E suite: it **drops and recreates** the database `blog_e2e_N` (same Postgres
  container, same credentials), applies the migrations, points the API's uploads at a fresh empty
  folder under `/tmp` for that instance, and starts API and Web on that instance's own ports
  (instance 1 keeps today's 5000/5002; instance N uses a fixed, documented offset). Without
  `--e2e` nothing changes (dev DB `blog`, `./uploads`, 5000/5002). `./stop-local.sh` stops the dev
  stack or a given instance (`--e2e --instance N`), and `--all` stops everything.
- Instance parameters (database name, uploads folder, ports, PID and log files) come from one
  function in the script; nothing else in the repo hard-codes them. The E2E suite reads the
  instance's base URLs from environment variables (`E2E_WEB_URL`, `E2E_API_URL`), defaulting to
  instance 1. This is the foundation for running several instances in parallel (next brief): two
  instances must be able to run at the same time without sharing anything except Postgres and the
  mock IdP. Show that: start instances 1 and 2, create a post through each, and show each database
  holds only its own.
- Never drop or modify the `blog` database from this script.
- Evidence: start with `--e2e`, show `blog_e2e` exists with the migrations applied and zero posts;
  start without it, show the dev DB untouched (row counts before and after).

## Item 2: the suite runs green on an empty database, and states what it needs

- Run the whole E2E suite against `--e2e` from empty. Every test that relied on data it didn't
  create (an existing category, "page 1 contains X", an existing image, ordering against other
  posts) gets fixed so it creates what it needs, or the shared baseline goes into a seed (item 3).
- No test may depend on another test having run, or on run order. Run the suite twice in a row on
  one `--e2e` start (so the second run sees the first's leftovers) and once on a fresh start: all
  green each time.
- Don't raise timeouts to get there (AGENTS.md: never make a failure go away by adding a wait).
  The 30 s waits added in R5.3 for the bloated DB go back to the suite's normal budget.
- List every test you changed and why in the report.

## Item 3: seed scripts

- `dev/seed-e2e.sh` (called by `start-local.sh --e2e` after the migrations): only the baseline data
  the suite genuinely shares, if any (a category the layout tests need, for example). Inserted
  through the API as the mock admin, never raw SQL, so it exercises the same validation as real
  use. If item 2 makes every test self-sufficient, this script may be empty apart from a comment
  saying so.
- `dev/reset-dev-data.sh`: drops and recreates the **dev** database `blog`, applies migrations and
  empties `./uploads`, after asking for confirmation (`--yes` skips it). Then optionally
  `dev/seed-dev-data.sh` loads a small, realistic sample for working on the site by hand: 3
  categories with icons, 6 tags, 8 published posts (one with a banner image and code blocks, one
  with a table), 1 draft, 1 scheduled post. Through the API, idempotent (running it twice doesn't
  duplicate).
- README "Local development": how to reset and seed.

## Item 4: everything uses the isolated stack

- AGENTS.md: agents run the E2E suite only against `./start-local.sh --e2e`; the shared dev DB is
  for the owner's hand testing. Remove the rules that told agents to leave the dev DB's data alone
  and to clean up probe data (no longer needed).
- `dev/opencode-sandbox.sh` `--verify`: start the stack with `./start-local.sh --e2e`. The owner
  reinstalls the root-owned copy afterwards (`dev/setup-agent-user.sh`); say so in the report.
- Instance 1 and the dev stack share ports 5000/5002, so they can't run at once; say that in the
  README.

## Out of scope

Running the E2E suite in parallel (next brief, 03h; this one only makes it possible). The slow
"render every row" pages (tags, sidebar, media): they get their own brief, with a
deliberate large-data seed. The page-view duplicate-key race. Anything else.
