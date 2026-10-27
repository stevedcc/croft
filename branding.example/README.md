# branding.example/

A complete example of a site's branding folder — the contract between a site and the blog
framework. Local dev and the E2E instances run with it (`start-local.sh` points
`Blog__BrandingPath` here), and it proves that overriding only some values works: its name,
words, primary colour, logo and fallback banner are its own, everything else is the
framework's built-in default.

## The folder format

```
branding.json          the words and the palette (every field and token optional)
logo.svg               sidebar/top-bar logo and the SVG favicon
favicon.ico            16x16, 32x32 and 48x48
apple-touch-icon.png   180x180
fallback-banner.svg    or fallback-banner.png / fallback-banner.jpg
masthead-banner.svg    optional; or masthead-banner.png / masthead-banner.jpg
site.css               optional; the site's own CSS, loaded last (see "site.css" below)
```

`branding.json` merges over the built-in defaults field by field and token by token: a field
or token you leave out keeps the default value. `description` is the home page's meta
description (empty falls back to `tagline`); `author` is the JSON-LD publisher name (empty
falls back to `name`). `masthead` (`true` or `false`, default `true`) switches the home page's
masthead on or off (see "The masthead" below).

`showPoweredBy` (`true` or `false`, default `true`) shows or hides "Running on Croft vX.Y.Z" in the
site footer, and `poweredByUrl` (an absolute `http` or `https` URL, default empty) makes "Croft"
a link to the project's repository; with no URL it is plain text. The footer also lists the pages
you place in the footer (the page editor's "Show in" control). With no footer pages and
`showPoweredBy` set to `false`, no footer is rendered at all.

`profiles` is an optional array of the owner's own profile links, one `{ "service", "url" }`
object each, rendered in the order you write them (the order is kept everywhere). `service` is
one of `mastodon`, `bluesky`, `github` or `linkedin` — anything else stops the Web app at
startup, like every other mistake in this file — and `url` must be an absolute `https` URL.
Each profile renders as a `<link rel="me" href="…">` in every page's head (Mastodon verifies
profile links by this), the post pages' structured data lists them as the author's `sameAs`
(this is what ties a post's author to their profiles for search engines), and the footer shows
them as small icons linking to the profiles.

`fediverseCreator` (e.g. `"@someone@example.social"`; it must look like `@user@host` or the
Web app stops at startup) renders `<meta name="fediverse:creator" content="…">` in every page's
head — what fediverse platforms show as the credited author on shared posts. With neither
field set, none of this renders.

Owner-side steps, on the account named in `profiles`:
- add the site's URL to a profile field of that account; Mastodon then shows the profile as
  verified (the green tick) via the `rel="me"` links above;
- allow this domain under the account's author-attribution settings, so shared posts credit the
  account (`fediverseCreator` is the handle it shows).

A malformed `branding.json`, an invalid colour or an unknown token stops the Web app at
startup with a message naming the field (a deployment mistake, like a missing
`Blog:BaseUrl`). A missing folder or file means the built-in defaults. When a checked text
pair (e.g. `text`/`bg`) falls below WCAG AA (4.5:1) in either mode, Web logs one warning per
pair and still starts.

## The palette tokens

Exactly these 18, each with a light and a dark value (`palette.light.<token>` /
`palette.dark.<token>`):

| token | what it colours |
|---|---|
| `bg` | the page background |
| `surface` | cards and panels |
| `text` | body text |
| `muted` | secondary text |
| `sidebar` | the sidebar and top bar |
| `sidebar-text` | text and icons in the sidebar |
| `primary` | links and primary controls |
| `primary-strong` | the active nav item |
| `primary-hover` | hover and pressed states |
| `primary-container` | highlighted containers |
| `on-primary` | text on `primary` |
| `secondary` | the second accent (strings, chips) |
| `secondary-container` | the second accent's containers |
| `on-secondary-container` | text on `secondary-container` |
| `on-secondary` | text on `secondary` |
| `code-bg` | code block background |
| `code-text` | code block text |
| `fallback-wash` | the wash under the fallback banner |

## site.css

An optional `site.css` next to `branding.json` styles the site without rebuilding the image.

- It is linked on **every page**, reading pages and staff pages alike, **after** the framework's
  stylesheets, so its rules win the cascade. Without the file there is no link and no request,
  and `/branding/site.css` is a 404.
- It is served as it is (no parsing or rewriting) and cached forever under a content-hash `?v=`
  (like the images): a changed file is a new URL after the Web app restarts, because the folder
  is read once at startup. A changed ConfigMap rolls the Web pods, which does exactly that.
- The Content-Security-Policy allows **same-origin stylesheets only** (`style-src` and
  `style-src-elem` are `'self'`), and `site.css` is one, so the policy is unchanged. An `@import`
  or a `url()` to another origin is blocked. `img-src` is `'self'` as well, so `data:` images are
  blocked too. The branding images' URLs change with their content (`?v=`), so `site.css`
  should not reference them either: images in `site.css` are not supported yet.
- The stable thing to override is the theme's CSS custom properties (the generated
  `/branding/theme.css`, one `--<token>` per palette token above, set on `:root` and again for
  dark mode), for example `--primary` (links and primary controls), `--bg` (the page background)
  and `--surface` (cards and panels).

`branding.example/site.css` carries a single rule, `:root { --site-css: "branding.example"; }`.
It changes nothing visible: it is the marker the E2E suite reads to prove the browser applied
the file, and a real site can delete it.

## The colour syntax

`#RGB`, `#RRGGBB`, `#RRGGBBAA`, `rgb(r,g,b)` and `rgba(r,g,b,a)` (channels 0-255, alpha
0-1). Anything else is rejected at startup, naming the field.

## The fallback banner

One logical file: the first of `fallback-banner.svg`, `fallback-banner.png`,
`fallback-banner.jpg` the site supplies is served for the fallback-banner name; supplying
none means the framework's default `fallback-banner.svg`: the Government / 42nd / Black Watch
tartan (generated by `dev/black-watch-banner.py`).

## The masthead

The first page of the home page opens with a slim strip carrying the site's `name` and
`description` (the `tagline` when there is none) in white over a banner: the site's identity
shows whatever banner the newest post has. It is not shown on page 2 and later or on any other
page. It is the home page's `h1`, and "Latest Posts" becomes an `h2` beneath it.

Its banner is the fallback banner by default. To give it a picture of its own, supply
`masthead-banner.svg`, `masthead-banner.png` or `masthead-banner.jpg` (the first of them is
served, validated, content-hashed and cached exactly like the fallback banner, and reaches the CSS
as `theme.css`'s `--masthead-banner` token). There is no framework default masthead file: with
none supplied the masthead uses the fallback banner. The text sits over the same dark wash and
gradient as the Featured card, so it reads on any dark or mid-tone picture; a very light picture
will not carry white text. Set `"masthead": false` in `branding.json` to leave the strip out.

## Supplying the folder

Three ways, all mounting the same folder at the app's `Blog:BrandingPath`:

1. **Kubernetes (kustomize):** a ConfigMap named `blog-branding` generated from the site's
   `branding/` folder and mounted at `/app/branding` in the Web deployment — the path the
   app reads by default.
2. **Docker/Podman:** bind-mount the folder read-only:
   `docker run -v ./branding:/app/branding:ro ...`
3. **A plain run:** point the setting at any folder:
   `Blog__BrandingPath=/path/to/branding dotnet run --project src/Croft.Web`
   (or `Blog__BrandingPath=` to run the built-in defaults).
