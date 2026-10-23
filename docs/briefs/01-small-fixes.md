# Brief 01: small correctness and security fixes

Read AGENTS.md first and follow it, including "Finishing a task". One commit per item.

## 1. Open redirect at /login

`src/Blog.Web/Program.cs`, the `/login` endpoint passes `returnUrl` straight into
`AuthenticationProperties.RedirectUri`, so `/login?returnUrl=https://evil.example` sends the user to
an external site after signing in.

- Add a small helper (e.g. `Blog.Web.LocalUrl.IsLocal(string?)`) with the same rules as MVC's
  `Url.IsLocalUrl`: must start with a single `/`, not `//` or `/\`; `~/` allowed; absolute URLs rejected.
- `/login` uses `returnUrl` only when it is local, otherwise `/`.
- Unit tests for the helper: `/admin`, `/post/x?y=1` allowed; `https://evil.example`, `//evil.example`,
  `/\evil.example`, `javascript:alert(1)`, empty or null rejected.
- E2E: log in via `/login?returnUrl=https://evil.example` and assert the browser ends on
  `http://localhost:5002/`, not evil.example.

## 2. Scheduling uses the server's time zone

`Components/Pages/Admin/PostEditor.razor`: the picker is labelled "UTC", but `_publishedAt` is a
date-only value with `Kind=Unspecified`, and `PublishAsync` calls `.ToUniversalTime()`, which
converts from the SERVER's local zone.

- Add a `MudTimePicker` beside the date picker. Combine date and time and mark the result as UTC with
  `DateTime.SpecifyKind(..., DateTimeKind.Utc)`. Never call `ToUniversalTime()` on picker values.
- When editing an existing post, show its `PublishedAt` as UTC (date and time).
- Put the combine logic in a small pure helper and unit-test it. Include the case where the server's
  local zone is not UTC (the helper must not depend on `TimeZoneInfo.Local`).
- Evidence: schedule a post for a specific UTC time via the editor and show that the API returns
  exactly that `publishedAt`.

## 3. Upload size limit (API)

`MediaController.Upload` has no size limit beyond Kestrel's default (~30 MB).

- Limit uploads to 10 MB with `[RequestSizeLimit]` and `[RequestFormLimits(MultipartBodyLengthLimit = ...)]`.
- API test: an 11 MB upload is rejected (413 or 400; state which, and why). A 1 MB valid PNG still succeeds.

## 4. Orphaned media files

Deleting a post cascades its `MediaAssets` rows but leaves the files on disk. A failed `SaveChanges`
after an upload also leaves the file behind.

- `PostService.DeleteAsync`: load the post's media file paths before deleting. After `SaveChanges`
  succeeds, delete each file via `IFileStorage.Delete`. Log and continue if a file delete fails;
  never fail the request because of it.
- `MediaService.UploadAsync`: if saving the DB row fails, delete the just-written file, then rethrow.
- API tests: deleting a post with a post-specific asset removes the file from the storage directory.
  An upload whose DB save fails leaves no file (simulate the failure with a test double or a
  constraint violation; explain which).

## Out of scope
Anything not listed above.
