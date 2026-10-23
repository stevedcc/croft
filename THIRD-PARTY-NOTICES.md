# Third-party notices

Croft itself is MIT-licensed ([LICENSE](LICENSE)). This file lists every third-party piece that is
**copied into this repository** (vendored files, fonts, icon paths, skills), with where it came
from, its licence and where the licence text lives. NuGet packages are not copied here; they are
restored at build time and carry their own licences (see "Not vendored" at the end).

## Bundled in the web app (`src/Croft.Web/wwwroot`)

| Piece | Source and version | Licence | Licence text and notes |
|---|---|---|---|
| highlight.js core and nine language grammars (bash, csharp, css, javascript, json, sql, typescript, xml, yaml) in `wwwroot/lib/highlightjs/` | [highlight.js](https://highlightjs.org/) 11.11.1, copyright (c) 2006 Ivan Sagalaev and Josh Goebel and other contributors | BSD-3-Clause | `wwwroot/lib/highlightjs/LICENSE`; the licence header is kept at the top of `core.min.js`. Unmodified minified builds. |
| Source Sans 3 (Regular, SemiBold) in `wwwroot/fonts/` | [Adobe Source Sans 3](https://github.com/adobe-fonts/source-sans) 3.052, copyright 2010-2022 Adobe | SIL Open Font License 1.1, Reserved Font Name "Source" | `wwwroot/fonts/OFL-SourceSans3.txt`. Latin subset (a Modified Version), renamed Croft Sans because the Reserved Font Name "Source" may not be used for it; built by `dev/fonts/subset-fonts.py` from the unmodified upstream files in `dev/fonts/source/`. |
| Source Serif 4 (Regular, Italic, SemiBold, Bold) | [Adobe Source Serif 4](https://github.com/adobe-fonts/source-serif) 4.005, copyright 2014-2023 Adobe | SIL Open Font License 1.1, Reserved Font Name "Source" | `wwwroot/fonts/OFL-SourceSerif4.txt`. Latin subset (a Modified Version), renamed Croft Serif because the Reserved Font Name "Source" may not be used for it; built by `dev/fonts/subset-fonts.py` from the unmodified upstream files in `dev/fonts/source/`. |
| JetBrains Mono (Regular) | [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) 2.304, copyright 2020 The JetBrains Mono Project Authors | SIL Open Font License 1.1 | `wwwroot/fonts/OFL-JetBrainsMono.txt`. Latin subset, built by `dev/fonts/subset-fonts.py` from the unmodified upstream file in `dev/fonts/source/` (no Reserved Font Name, so it keeps its name). |
| Search glyph (the magnifier in the search field, `Search.razor`) | Google [Material Icons](https://fonts.google.com/icons) `search` (the 24 px filled path, as published by Material Design) | Apache License 2.0 | <https://www.apache.org/licenses/LICENSE-2.0>; copyright Google LLC. Path used as published. |

The font files are served from the site's own origin. The Source Sans 3 and Source Serif 4 files
are Modified Versions (Latin subsets), so they carry the names Croft Sans and Croft Serif, as the
OFL's Reserved Font Name condition (no modified version under that name) requires; the unmodified
upstream files stay in `dev/fonts/source/`. Each family's OFL text stays next to its files, as the
licence requires.

## Profile icons (the footer's icon links)

The SVG path data inlined in `src/Croft.Web/Components/Layout/ProfileIcon.razor` comes from
these sources. Nothing is fetched at runtime; the paths are part of the source tree.

| Icon | Source | Licence | Notes |
|---|---|---|---|
| Mastodon | [Simple Icons](https://simpleicons.org/), icon `mastodon` | CC0-1.0 (public-domain dedication) | Path used as published. |
| Bluesky | Simple Icons, icon `bluesky` | CC0-1.0 | Path used as published. |
| GitHub | Simple Icons, icon `github` | CC0-1.0 | Path used as published. |
| neutral link icon (used for LinkedIn) | [Material Design Icons](https://pictogrammers.com/library/mdi/icon/link/), icon `link` | Apache License 2.0 | **Not** the LinkedIn mark. Simple Icons no longer carries the LinkedIn icon (trademark request), and no LinkedIn mark with a licence allowing redistribution in an MIT project could be found, so LinkedIn's entry carries a neutral link icon instead. Path used as published. |

Simple Icons' CC0 dedication: <https://github.com/simple-icons/simple-icons/blob/develop/LICENSE.md>.
Material Design Icons' Apache-2.0 licence: <https://github.com/Templarian/MaterialDesign/blob/master/LICENSE>.

The trademarks belong to their respective owners; the icons identify the owner's profiles and
imply no endorsement.

## Vendored agent skills (`.claude/skills/`)

Thirteen Microsoft skills (Blazor, EF Core, Web API, refactoring, performance, test review, the
.NET 11 upgrade) are copied from <https://github.com/dotnet/skills> at the commit recorded in
`.claude/skills/MICROSOFT-SKILLS.md`, licence MIT, copyright .NET Foundation and Contributors. The licence
text is `.claude/skills/MICROSOFT-SKILLS-LICENSE`. They are unchanged except that the Blazor skills
carry a `blazor-` name prefix and renamed cross-references (disclosed in `MICROSOFT-SKILLS.md`,
which also lists each skill). The other skills in that folder (`writing-dotnet10-code`,
`writing-dotnet11-code`) are this project's own.

## Original in-house assets

These need no notice but are listed so a reader does not wonder. `wwwroot/branding-default/`: the
logo is three rounded rectangles and the fallback banner is a Black Watch tartan generated by
`dev/black-watch-banner.py` (a tartan of 1739, no copyright). `branding.example/`: a logo of
rounded rectangles and a dot-pattern banner, both plain SVG written for the example.
`wwwroot/css/highlight-theme.css` (built from the palette tokens) and `wwwroot/js/*.js` are
written for this project. The contents-panel chevron is a three-point line.

## Not vendored

NuGet packages are referenced, not copied, and keep their own licences: for example MudBlazor
(MIT), Markdig (BSD-2-Clause), AngleSharp (MIT; parses the HTML of Mastodon replies before it is reduced to p, br and a), Npgsql (PostgreSQL licence), OpenTelemetry (Apache-2.0), and for the
tests YamlDotNet (MIT), bUnit (MIT), xUnit (Apache-2.0), NUnit (MIT), Playwright (Apache-2.0),
Testcontainers (MIT), AwesomeAssertions (Apache-2.0) and Deque.AxeCore.Playwright (MPL-2.0).
The API decodes and re-encodes the avatars of social replies with SkiaSharp (MIT; the managed wrapper) and
SkiaSharp.NativeAssets.Linux.NoDependencies (MIT; the native Skia library for linux-x64 and linux-arm64, built from
Google's Skia, BSD-3-Clause). Both are restored at build time, not copied here.
MudBlazor's `Icons.Material.Filled` (Material Icons path data, Apache-2.0) supplies the category
icon picker's glyphs from the package at runtime; the paths are not copied into this repository.
