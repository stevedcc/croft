# Brief 03e: SEO basics

Read AGENTS.md first and follow it, including "Finishing a task". Use `subagent-driven-development`
with this brief's items as the tasks; one commit per item, TDD, break-it-once, E2E twice in a row at
the end, then the final whole-branch review.

**Why:** crawford.dev is live (2026-10-08). Pages are server-rendered and have `<html lang="en">`,
but there is no meta description (the editor's "Search & link-preview description", stored as the
post's `Excerpt`, is never output), no canonical URL, no Open Graph or Twitter Card tags, no
structured data, and `/robots.txt`, `/sitemap.xml` and any feed all return 404.

## Item 1 (reference task): the post page's head

- New setting `Blog:BaseUrl` (production: `https://crawford.dev`, set in `blog-web-config` in
  `k8s/configmap.yaml`; development: the local Web URL). All absolute URLs below come from it, never
  from the request's host.
- One component that writes a page's SEO head (inside `<HeadContent>`), with parameters for the
  title, description, canonical path, image, page type and dates. Post page (`/post/{slug}`):
  - `<meta name="description">`: the post's `Excerpt`; if empty, its `Subtitle`; if that's empty
    too, the start of its summary text (plain text, about 160 characters, cut at a word).
  - `<link rel="canonical" href="{BaseUrl}/post/{slug}">`.
  - Open Graph: `og:type` `article`, `og:title`, `og:description`, `og:url` (the canonical URL),
    `og:site_name` (`Blog:Name`), `og:locale` `en_GB`, `og:image` (the banner image as an absolute
    URL; no image tag when there is no banner), `article:published_time` and
    `article:modified_time` (ISO 8601, UTC), `article:tag` per tag, `article:section` the category.
  - Twitter: `twitter:card` `summary_large_image` with a banner, `summary` without.
  - JSON-LD `BlogPosting`: `headline`, `description`, `datePublished`, `dateModified`, `author`
    (`Person`, the post's author name), `image` when there is a banner, `mainEntityOfPage` (the
    canonical URL), `publisher` (`Blog:Name`). Serialise with `System.Text.Json` so titles with
    quotes or `</script>` can't break out of the script tag (test it).
  - Drafts and scheduled posts seen by an Admin/Editor get `<meta name="robots" content="noindex">`.
- Tests (bUnit or E2E against the rendered HTML): every tag above for a post with and without a
  banner and with and without an excerpt; the JSON-LD parses as JSON and survives a title
  containing `"` and `</script>`. Break-it-once: drop the description fallback; its test fails.

## Items 2–4 (apply pattern, reference = item 1's commit)

Run these through the `pattern-applier` subagent (AGENTS.md), giving it item 1's commit as the
reference. It may decline any of them with a reason; declined items go back to the normal implementer.

2. Home page (`/`): description = `Blog:Tagline`; canonical `{BaseUrl}/`; `og:type` `website`;
   JSON-LD `Blog` (name, description, url). Page 2+ of the listing: canonical to itself.
3. Category page (`/category/{slug}`): description "Posts in {name} on {Blog:Name}"; canonical;
   `og:type` `website`.
4. Tag page (`/tag/{slug}`): description "Posts tagged {name} on {Blog:Name}"; canonical;
   `og:type` `website`.

Each with the same kind of test as item 1 for its page.

## Item 5: noindex where search engines don't belong

`<meta name="robots" content="noindex">` on the search page, the not-found page, the error page and
every admin and editor page. Not-found responses keep their 404 status. Test one of each kind.

## Item 6: robots.txt

`GET /robots.txt` (text/plain): allow everything, disallow `/admin`, `/editor`, `/login`,
`/logout`, `/signin-oidc`, `/signout-callback-oidc` and `/api/`, and
`Sitemap: {BaseUrl}/sitemap.xml`. Test the exact body.

## Item 7: sitemap.xml

`GET /sitemap.xml` (application/xml), per the sitemaps.org protocol: the home page, every
**published** post (`lastmod` = its `UpdatedAt`, ISO 8601 date), every category and tag that has at
least one published post. No drafts, no scheduled posts, no admin pages. Tests: a published post is
listed with its `lastmod`; a draft and a scheduled post are not; the XML is well-formed with the
sitemap namespace.

## Item 8: Atom feed

`GET /feed.xml` (`application/atom+xml`): the 20 newest published posts as an Atom 1.0 feed (title,
link to the canonical URL, `id` = the canonical URL, `updated`, `published`, author, summary = the
description from item 1, and categories from the post's category and tags). Every page gets
`<link rel="alternate" type="application/atom+xml" title="{Blog:Name}" href="{BaseUrl}/feed.xml">`.
Tests: well-formed Atom with the right namespace; drafts and scheduled posts absent; newest first;
a title with `&` and `<` is escaped correctly.

## Out of scope

`hreflang` (comes with translations), Search Console / Bing verification (the owner does that),
image resizing for social cards, anything else.
