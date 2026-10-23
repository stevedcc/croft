# Brief 07e: long posts can be written and edited without losing text

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review.

## Item 1: the editor's connection carries any post up to the content limit

**Now (found by brief 07d, `tests/Croft.Web.E2E/PostRailStickyTests.cs:95-102`):** the Markdown
editor (`Components/Admin/MarkdownEditor.razor`) sends the **whole** textarea value to the server
on every `input` event. Blazor Server's hub accepts at most `MaximumReceiveMessageSize` bytes per
message (default 32 KB; `Program.cs` never sets it), so once a post's text is longer than about
16K characters one event aborts the circuit and the text is lost: a paste of a long draft, and
also **every keystroke** in a post that has grown past the limit. There is no autosave, and nothing
limits a post's length in the API or the database.

**Confirm first and report:** the largest text (ASCII characters, and separately a text of `ä`
and `€`, plus one with many `"` and `\` and newlines, which JSON escapes) for which one `input`
event still reaches the server, and the message bytes per character for each. This decides the
limit below; the brief's own numbers are estimates until you measure them.

**Design:**
- One content limit, `PostContent.MaxLength = 200_000` characters (`Croft.Shared`), used by the
  API and the editor. A long technical post is about 60K characters; 200K leaves room.
- The API rejects a create or update whose `Content` is longer with **400** and a validation
  problem naming the field (the same shape as the API's other validation errors), and stores
  nothing.
- `Program.cs` sets the hub's `MaximumReceiveMessageSize` (via `.AddHubOptions(...)` on the
  interactive server components) to the smallest power-of-two bytes that carries one `input` event
  of `MaxLength` characters of the **worst** text you measured, with headroom ≥ 25%. Show the
  arithmetic in the commit message.
- The editor shows the count when the text is over 90% of the limit (`12,345 / 200,000
  characters`, below the textarea, muted UI font) and, when it is over the limit, an error line
  (`role="alert"`) and the page's Save/Publish buttons are disabled. A text over the limit is
  still sent to the server and kept in the textarea (the limit must never lose text).

**Tests:**
- E2E (each test creates its own post through the admin UI): set the textarea to texts of 20K,
  60K and 200K characters in **one** input event (`FillAsync`), then type one more character, save;
  reload the editor: the textarea holds exactly the text (length and content); the circuit never
  reconnected (`#components-reconnect-modal` never opened, check with a `MutationObserver`
  installed before the fill). Repeat 60K with the worst-case text you measured.
- E2E: after the 60K post is saved, type 20 characters one key at a time
  (`PressSequentiallyAsync`) and save: the stored text ends with them.
- E2E: 200,001 characters: the alert shows, Save is disabled, the textarea still holds all
  200,001 characters; delete one: the alert goes and Save works.
- API test: `Content` of 200,001 characters gives 400 with the field named and no row written;
  200,000 is stored.
- Break-it-once: remove the `AddHubOptions` line: the 60K E2E must fail. Remove the API check:
  its test must fail.

## Item 2: 07d's sticky-rail fixtures return to what 07d specified

`PostRailStickyTests` shortened its section bodies to one ~26-word sentence because of this bug.
Restore brief 07d's fixture (12 sections of ~150 words, and the 40-section post with the same
~150-word bodies), remove the workaround comments, and run the file: it must pass unchanged
otherwise. Apply the same to any other test that shortened its text for this reason (grep for
"16K" and "circuit"; list what you found).

## Out of scope

Autosave, debouncing input events, collaborative editing, anything in the public pages.
