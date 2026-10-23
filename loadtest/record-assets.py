"""Record what a fresh Chromium fetches from the site's own origin for each page: the asset lists
visit-mix.js replays. Only GET requests to the same origin are kept, so the page-view beacon (a
POST, which would count a visit) and third-party requests are left out.

    pip install playwright && python3 -m playwright install chromium
    python3 loadtest/record-assets.py https://blog.example / /post/hello /category/news > loadtest/assets.json
"""
import asyncio
import json
import sys

from playwright.async_api import async_playwright


async def record(base: str, paths: list[str]) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    async with async_playwright() as pw:
        browser = await pw.chromium.launch()
        for path in paths:
            # A new context per page: an empty cache, like a first-time visitor.
            context = await browser.new_context(viewport={"width": 1280, "height": 800})
            seen: list[str] = []
            context.on(
                "requestfinished",
                lambda r: seen.append(r.url) if r.method == "GET" and r.url.startswith(base + "/") else None,
            )
            page = await context.new_page()
            await page.goto(base + path, wait_until="load")
            await page.wait_for_timeout(1500)  # late requests: fonts, lazy images
            out[path] = list(dict.fromkeys(u[len(base):] for u in seen if u != base + path))
            await context.close()
        await browser.close()
    return out


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    base = sys.argv[1].rstrip("/")
    print(json.dumps(asyncio.run(record(base, sys.argv[2:])), indent=1))
