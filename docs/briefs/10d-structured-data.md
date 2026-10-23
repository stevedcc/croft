# Brief 10d: complete structured data for posts

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:**
- Search engines see each post's tags, category and place in the site in its structured data
  (JSON-LD).
- Google's Rich Results Test shows a valid `BlogPosting` and a valid `BreadcrumbList`, with no
  warnings that Croft can fix.

**Evidence (`src/Croft.Web/Components/Layout/SeoHead.razor`):**
- The post page's `BlogPosting` JSON-LD carries headline, description, dates, author, image and
  publisher.
- The post's tags and category reach the page only as Open Graph meta tags
  (`article:tag`, `article:section`, lines ~53–59), never in the JSON-LD.
- There is no `BreadcrumbList` anywhere.
- No `<meta name="keywords">` is emitted, which is right: Google ignores it and Bing treats a
  stuffed one as spam. **Keep it that way.**

## Item 1: `keywords` and `articleSection` in the post's BlogPosting

**Do:**
- In `SeoHead`'s BlogPosting payload, add:
  - `"keywords"`: the post's tag names joined with `", "`, in the order SeoHead already
    receives them. Omit it when there are no tags.
  - `"articleSection"`: the category's name. Omit it when there is none.
- Use the values SeoHead already gets (`Tags`, `Section`). No new API calls.
- The serialisation stays safe: the existing `</script>` escaping test covers it. Add a tag
  named `</script><b>` to it.

**Tests (bUnit / Web unit, parsing the JSON-LD with `System.Text.Json`):**
- A post with tags "kubernetes" and "raspberry-pi" and category "Optimisation": the JSON parses,
  `keywords == "kubernetes, raspberry-pi"`, and `articleSection == "Optimisation"`.
- A post with no tags and no category: neither property exists. Assert absence, not empty.
- `</script>` in a tag name doesn't end the script block (the page still has exactly one
  `application/ld+json` block per type, and it parses).
- No `<meta name="keywords">` anywhere on a post page.
- Break-it-once: drop `keywords` from the payload; the first test must fail.

## Item 2: a BreadcrumbList on post pages

**Do:**
- On the **post** page only, a second JSON-LD block of `@type` `BreadcrumbList`:
  1. position 1: the site name, at `{Blog:BaseUrl}/`;
  2. position 2: the category's name, at `{Blog:BaseUrl}/category/{slug}`, **only when the post
     has a category**;
  3. the last position: the post's title, at its canonical URL.

  Without a category it's two items (Home → Post).
- **All URLs are absolute and built from `Blog:BaseUrl`**, exactly as the canonical URL is today,
  never from the request's host.
- SeoHead needs the category's **slug**. Add a parameter, e.g. `SectionSlug`, and pass it from
  `Post.razor`, which has the category DTO. Other pages pass nothing and render no breadcrumb.
- Static pages (`StaticPage.razor`), Home, category and tag pages get **no** BreadcrumbList.

**Tests:**
- bUnit: a post with a category gives exactly three `ListItem`s with positions 1, 2 and 3 and
  the expected absolute URLs. With `Blog:BaseUrl` set to `https://example.test`, every `item`
  starts with it, even when the request host differs.
- Without a category, two items, with positions 1 and 2.
- Home, a category page, a tag page and a static page have no BreadcrumbList.
- **E2E:** on a published post page, both JSON-LD blocks are present and parse, with the right
  types.
- Break-it-once: build the category URL from the request host instead of `Blog:BaseUrl`; the
  `example.test` test must fail.

## Item 3: check against Google's documented requirements

- Compare the BlogPosting and BreadcrumbList output with Google's structured-data documentation
  for Article and Breadcrumb (required and recommended properties).
- List any **recommended** property Croft can supply from data it already has, for example the
  author's `url` from `Blog:BaseUrl` or the publisher's `logo` from the branding logo. Add those
  that need no new data or configuration, with tests.
- Put the rest in your report; don't add configuration for them.
- **No network access is needed or allowed** for this. If you can't read the documentation from
  inside the sandbox, say so, and list only what you know from the schema.org vocabulary, marked
  as unchecked.

## Out of scope

- `<meta name="keywords">` (never).
- JSON-LD for category or tag pages (`CollectionPage`), search pages or the home page beyond
  today's `Blog`.
- Any CSP change. JSON-LD blocks are data, not executed script; a parallel brief (10c) tightens
  the CSP.
- The footer (brief 10f, in parallel). Don't touch `MainLayout`.
