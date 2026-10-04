#!/usr/bin/env python3
"""Generates the Pluto application icon.

Produces a classic desktop-style icon: a beveled silver tile with the
retro window chrome language used by the Classic theme, so the icon and the
app read as the same product. Replace Assets/pluto.png with your own artwork
and this script becomes unnecessary - the build only needs the file.

Usage:  python3 Scripts/make_icon.py [output.png] [size]
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent

# Classic Windows 9x palette
FACE = (192, 192, 192, 255)        # C0C0C0 silver
HIGHLIGHT = (255, 255, 255, 255)   # FFFFFF 3D highlight
LIGHT = (223, 223, 223, 255)       # DFDFDF 3D light
SHADOW = (128, 128, 128, 255)      # 808080 3D shadow
DARK = (0, 0, 0, 255)              # 000000 3D dark shadow
TITLE_A = (0, 0, 128, 255)         # 000080 title bar start
TITLE_B = (16, 132, 208, 255)      # 1084D0 title bar end
DESKTOP = (0, 128, 128, 255)      # 008080 desktop teal


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(4))


def draw_icon(size: int) -> Image.Image:
    s = size
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Desktop-teal backdrop with a subtle vignette so the tile reads as a window
    # sitting on a desktop rather than a floating square.
    d.rectangle([0, 0, s - 1, s - 1], fill=DESKTOP)
    for i in range(s // 6):
        alpha = int(70 * (i / (s / 6)))
        d.rectangle([i, i, s - 1 - i, s - 1 - i],
                    outline=(0, 0, 0, alpha))

    # Window tile inset from the edge.
    m = max(2, round(s * 0.10))
    x0, y0, x1, y1 = m, m, s - m - 1, s - m - 1

    # Raised bevel: dark bottom-right, then shadow, then highlight top-left.
    d.rectangle([x0, y0, x1, y1], fill=FACE)
    d.line([(x0, y0), (x1, y0), (x1, y1)], fill=DARK, width=max(1, s // 64))
    d.line([(x0, y0), (x0, y1), (x1, y1)], fill=SHADOW, width=max(1, s // 96))
    d.line([(x0 + 1, y0 + 1), (x1 - 1, y0 + 1), (x1 - 1, y1 - 1)], fill=LIGHT, width=max(1, s // 128))
    d.line([(x0 + 1, y0 + 1), (x0 + 1, y1 - 1), (x1 - 1, y1 - 1)], fill=HIGHLIGHT, width=max(1, s // 128))

    # Title bar with the classic horizontal gradient.
    tb_h = max(3, round((y1 - y0) * 0.17))
    ty0 = y0 + max(1, s // 64)
    ty1 = ty0 + tb_h
    for y in range(ty0, ty1):
        d.line([(x0 + 1, y), (x1 - 1, y)], fill=lerp(TITLE_A, TITLE_B, (y - ty0) / max(1, tb_h - 1)))

    # The letter P, drawn as blocky pixels so it reads at 32px.
    px = max(1, s // 32)
    glyph_w = px * 5
    glyph_h = px * 7
    gx = x0 + (x1 - x0 - glyph_w) // 2
    gy = ty1 + (y1 - ty1 - glyph_h) // 2 + px

    # P: vertical stem, top bar, right stem, middle bar
    rects = [
        (0, 0, px, glyph_h),                 # stem
        (px, 0, glyph_w, px),                 # top bar
        (glyph_w - px, 0, glyph_w, px * 4),   # right stem upper
        (px, px * 3, glyph_w - px, px * 4),   # middle bar
    ]
    for rx, ry, rw, rh in rects:
        d.rectangle([gx + rx, gy + ry, gx + rx + rw - 1, gy + ry + rh - 1], fill=DARK)

    return img


def main() -> int:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "Assets" / "pluto.png"
    size = int(sys.argv[2]) if len(sys.argv) > 2 else 512

    out.parent.mkdir(parents=True, exist_ok=True)
    icon = draw_icon(size)
    icon.save(out)

    # A 256px copy is what AppImageKit wants for the top-level icon.
    icon.resize((256, 256), Image.LANCZOS).save(out.with_name("pluto-256.png"))

    print(f"wrote {out} ({size}x{size}) and {out.with_name('pluto-256.png')} (256x256)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())