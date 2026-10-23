# Brief 03k: images open to search engines, icons back in the category picker

Read AGENTS.md first and follow it, including "Finishing a task". One commit per item, TDD,
break-it-once, E2E twice in a row at the end, then the final whole-branch review.

## Item 1: crawlers may fetch published images

`/robots.txt` (`src/Blog.Web/Program.cs`) has `Disallow: /api/`, and every image is served from
`/api/media/{id}`. So Google Images can't index any image, and X/Twitter (whose crawler obeys
robots.txt) can't load a post's `og:image` for its link preview.

- Add `Allow: /api/media/` directly above `Disallow: /api/`. Google picks the longest matching
  rule, and some crawlers take the first match, so this order is right for both. The rest of the
  file stays as it is.
- This doesn't make anything public that isn't already. `GET /api/media/{id}` answers anonymous
  requests for a draft or scheduled post's image with 403, and `MediaProxyTests.
  DraftImage_AdminCanView_AnonymousGetsForbidden` guards that. Don't change that test; it must
  still pass.
- Tests: `RobotsTxtTests.RobotsTxt_ReturnsExactBodyAsPlainText` asserts the exact body. Its
  expected text gains the new line because this brief changes the rules; say so in the commit
  message (AGENTS.md rule 5). Break-it-once: put the `Allow` line below the `Disallow`; the test
  must fail.

## Item 2: category icons in the post editor's picker

Brief 03i replaced the editor's category `MudSelect` with a `MudAutocomplete` (the list can hold
hundreds of categories). The old select showed each category's icon next to its name
(`CategoryIcon`, `src/Blog.Web/Components/Admin/CategoryIcon.razor`); the autocomplete shows
names only.

- In `PostEditorForm.razor`, give the autocomplete's options the icon and the name, as the old
  select's items did (`<span class="post-form__category"><CategoryIcon Name="@category.Icon"
  Size="16" /> @category.Name</span>`), through MudAutocomplete's item template.
- If MudAutocomplete can also show the selected category's icon in the field itself without
  custom JavaScript, do that too; if not, leave the field as text and say so in the report.
- Keep everything 03i established: the server-side search, `MaxItems` off, Clearable, the
  `AdornmentAriaLabel`, and the accessibility checks passing.
- Tests: a bUnit test that an option renders the category's icon and name; break-it-once by
  removing the template. Run the E2E accessibility tests that cover the editor.

## Out of scope

An image sitemap, image resizing for social cards, alt-text changes, anything else.
