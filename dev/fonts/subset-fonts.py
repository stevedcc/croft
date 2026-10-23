#!/usr/bin/env python3
"""Builds the web fonts in src/Croft.Web/wwwroot/fonts from the full fonts in dev/fonts/source.

Each font is cut down to the Latin range (Google Fonts' "latin" subset: ASCII, Latin-1 with the
German letters, typographic punctuation, arrows, the euro sign), all OpenType layout features
kept. Characters outside it fall back to the next font in the CSS stack.

Source Serif 4 and Source Sans 3 carry the Reserved Font Name "Source" (SIL OFL 1.1, see the
OFL-*.txt files). A subset is a Modified Version, which may not use that name, so they are
renamed Croft Serif and Croft Sans. JetBrains Mono has no Reserved Font Name and keeps its own.

    python3 dev/fonts/subset-fonts.py        (needs fontTools and brotli: pip install fonttools brotli)
"""
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "dev" / "fonts" / "source"
TARGET = ROOT / "src" / "Croft.Web" / "wwwroot" / "fonts"

LATIN = ("U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,"
         "U+2000-206F,U+20AC,U+2122,U+2190-2199,U+2212,U+2215,U+FEFF,U+FFFD")

# source file -> (output file, new family name or None to keep the font's own)
FONTS = {
    "SourceSerif4-Regular.woff2": ("CroftSerif-Regular.woff2", "Croft Serif"),
    "SourceSerif4-Italic.woff2": ("CroftSerif-Italic.woff2", "Croft Serif"),
    "SourceSerif4-SemiBold.woff2": ("CroftSerif-SemiBold.woff2", "Croft Serif"),
    "SourceSerif4-Bold.woff2": ("CroftSerif-Bold.woff2", "Croft Serif"),
    "SourceSans3-Regular.woff2": ("CroftSans-Regular.woff2", "Croft Sans"),
    "SourceSans3-SemiBold.woff2": ("CroftSans-SemiBold.woff2", "Croft Sans"),
    "JetBrainsMono-Regular.woff2": ("JetBrainsMono-Latin.woff2", None),
}


def rename(font: TTFont, family: str) -> None:
    names = font["name"]
    style = names.getDebugName(17) or names.getDebugName(2) or "Regular"
    full = f"{family} {style}"
    postscript = f"{family.replace(' ', '')}-{style.replace(' ', '')}"
    for record in list(names.names):
        if record.nameID in (1, 16, 21):
            record.string = family
        elif record.nameID == 4:
            record.string = full
        elif record.nameID == 6:
            record.string = postscript
        elif record.nameID == 3:
            record.string = f"{postscript};croft-subset"
        elif record.nameID in (18, 20):
            names.removeNames(nameID=record.nameID)


def main() -> None:
    options = subset.Options()
    options.flavor = "woff2"
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.name_languages = ["*"]
    for source, (output, family) in FONTS.items():
        font = subset.load_font(str(SOURCE / source), options)
        subsetter = subset.Subsetter(options)
        subsetter.populate(unicodes=subset.parse_unicodes(LATIN))
        subsetter.subset(font)
        if family:
            rename(font, family)
        subset.save_font(font, str(TARGET / output), options)
        print(f"{output}: {(TARGET / output).stat().st_size} bytes")


if __name__ == "__main__":
    main()
