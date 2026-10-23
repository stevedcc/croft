# dev/screenshot-gate — the visual-regression gate

Tooling for the screenshot comparison (brief 07f item 7, ruling R17): capture a fixed set of
page surfaces on two checkouts and prove the pixels did not move. It is not part of
`Croft.slnx` or the E2E suite; it is run by hand, and it is safe to keep around for any
change that claims to alter nothing on any page.

## The set

Seven surfaces, each at **390x844** and **1440x900**, in **light and dark** (28 PNGs per
side): `admin-posts`, `admin-media`, `post-editor` (the fixed draft, on load), `home`,
`post-banner` (published post with the repository's fixed `test-image.png` as banner),
`post-nobanner` (published post without banner — the fallback banner), `search`
(the empty state). Everything rendered on them is a constant of the seed: fixed titles,
body text, tag/category names, fixed past publish dates ("10 January 2026"), the fixed
banner file. There is no run-keyed text (the suite's ScreenshotTests fixtures carry run ids
by design and are not comparable across runs) and no clock text — the draft's "Saved hh:mm"
label only appears after an in-session save, so the editor is captured on load. The admin
surfaces capture before any public post page is visited, so the posts grid's Views column
reads 0 no matter how beacons behave. Animations are disabled in the screenshot options.

## The flow (per side of the comparison)

```bash
# start an isolated instance of THIS checkout (fresh blog_e2e_N each time)
./start-local.sh --e2e --instance 7   # instance 7: API 5060 / Web 5062

# deterministic fixtures (fixed names and dates) + fixtures.json for the capture step
API_BASE_URL=http://localhost:5060 GATE_OUT=/tmp/opencode/screenshot-gate/baseline \
    dev/screenshot-gate/seed.sh

# capture the 28 PNGs
GATE_OUT=/tmp/opencode/screenshot-gate/baseline E2E_WEB_URL=http://localhost:5062 \
    dotnet test dev/screenshot-gate/capture

./stop-local.sh --e2e --instance 7
```

Then check out the other side (`git checkout <commit>` — a detached, temporary checkout;
the branch ref never moves and the tree must be clean), repeat, and compare:

```bash
GATE_BASE=/tmp/opencode/screenshot-gate/baseline \
GATE_BRANCH=/tmp/opencode/screenshot-gate/branch \
GATE_DIFF=/tmp/opencode/screenshot-gate/diff \
    dotnet test dev/screenshot-gate/compare
```

The comparison decodes both sides with the E2E project's `PngPixelDecoder`
(`tests/Croft.Web.E2E/PngPixelDecoder.cs`, compiled in), prints one line per
(surface, width, theme) with the differing pixel count and the bounding box, and writes
`{surface}-{width}-{theme}-crop-{baseline,branch,diffmap}.png` to `GATE_DIFF` for any
difference. The test fails on any difference; expected result is zero everywhere.

## Determinism checklist

- Fresh database per side (`start-local.sh --e2e` drops and recreates `blog_e2e_N`), seeded
  identically by `seed.sh` — run-id text would make the runs incomparable and is not used.
- The capture browser launches with the screenshot-stable flags (`--disable-gpu`,
  `--disable-lcd-text`, `--disable-skia-runtime-opts`, `--disable-partial-raster`,
  `--font-render-hinting=none`, `--force-color-profile=srgb`): without them Chromium flips
  between its GPU and software raster paths per capture and text, borders and rounded
  corners come out with one of two antialiasings (measured as 15-22 differing pixels on
  some surfaces, bimodal between runs). With the flags, two fresh cycles of
  start → seed → capture compare pixel-identical (verified).
- One browser context per capture (own viewport and theme preference); dark waits for the
  rendered dark palette (the light first-paint flash is not the state to capture).
- Surfaces are captured in one fixed order; admin surfaces first.
- The comparison tooling builds anywhere; the capture harness references no repository code,
  so it compiles against the baseline checkout too. The compare step links
  `PngPixelDecoder.cs` from the repository (`PngPixelDecoderPath` overrides the path).
