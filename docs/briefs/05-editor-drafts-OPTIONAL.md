# Brief 05 (OPTIONAL, decide first): let Editors create their own drafts

Only hand this over if Editors (e.g. the AI service account) should be able to create posts. The
design spec says Editors can "create/edit own drafts", but `POST /api/posts` is Admin-only, so an
Editor can never own a draft and the "own drafts" update rule never applies.

Read AGENTS.md first and follow it, including "Finishing a task".

## Changes (API only; no UI for Editors)
- `POST /api/posts`: allow `Admin,Editor`. Posts created by an Editor are always `Draft`, with
  `AuthorId` and `AuthorName` taken from the token as today.
- Editors still can't publish (`/publish` stays Admin-only) or delete. They can update only their
  OWN drafts (existing rule: someone else's post → 404, own non-draft → 403).
- Editors may upload post-specific media to their own drafts only: `POST /api/media` allows Editor
  when `scope=PostSpecific` and the post is their own draft (otherwise 403). Global uploads stay Admin-only.

## Tests
- Editor creates a draft → 201 and Status=Draft.
- An Editor updates their own draft → 200; updates another Editor's draft → 404; tries to publish → 403.
- Editor media upload: own draft → 201; someone else's post → 403; Global scope → 403.

## Out of scope
UI changes, Editor deletion rights.
