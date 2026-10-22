"""Build transparent XRTAK launcher icons from the shield logo."""
from __future__ import annotations

import collections
import os
from pathlib import Path

from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
SRC = Path(
    r"C:\Users\amosh\.cursor\projects\g-CopIX-TAKXR-AndroidXR\assets"
    r"\c__Users_amosh_AppData_Roaming_Cursor_User_workspaceStorage_"
    r"8d350ba4ab11fb0e81cbe5c7021833de_images_image-37206be0-0c96-4991-84c6-949a149e882b.png"
)
# Fallback if Cursor workspace asset path moves
if not SRC.is_file():
    SRC = ROOT / "docs" / "logo.png"

OUT_DIR = ROOT / "Assets" / "Brand"
DOCS_LOGO = ROOT / "docs" / "logo.png"
ICON_MASTER = OUT_DIR / "XRTAK-icon.png"
ICON_FG = OUT_DIR / "XRTAK-icon-foreground.png"  # adaptive foreground (safe zone)


def analyze(im: Image.Image) -> None:
    w, h = im.size
    alphas = collections.Counter()
    opaque_dark = opaque_other = 0
    for y in range(0, h, 4):
        for x in range(0, w, 4):
            r, g, b, a = im.getpixel((x, y))
            alphas[a] += 1
            if a > 200:
                if r + g + b < 40:
                    opaque_dark += 1
                else:
                    opaque_other += 1
    print("size", w, h, "bbox", im.getbbox())
    print("opaque_dark", opaque_dark, "opaque_other", opaque_other)
    print("alpha top5", alphas.most_common(5))


def flood_clear_exterior(im: Image.Image, dark_thresh: int = 28, alpha_thresh: int = 40) -> Image.Image:
    """
    Clear near-black pixels that are connected to the image border (outside the
    shield). Keeps the black XRTAK banner + eagle inside the white shield rim.
    Softens the cut with a slight alpha feather for clean launcher AA.
    """
    im = im.convert("RGBA")
    w, h = im.size
    px = im.load()

    dark_limit = int(dark_thresh)
    alpha_limit = int(alpha_thresh)

    def is_exterior_candidate(x: int, y: int) -> bool:
        r, g, b, a = px[x, y]
        if a < alpha_limit:
            return True
        # Near-black (and already-transparent-ish) outside the white rim.
        return r <= dark_limit and g <= dark_limit and b <= dark_limit

    visited = bytearray(w * h)
    stack: list[tuple[int, int]] = []

    def push(x: int, y: int) -> None:
        if x < 0 or y < 0 or x >= w or y >= h:
            return
        i = y * w + x
        if visited[i]:
            return
        if not is_exterior_candidate(x, y):
            return
        visited[i] = 1
        stack.append((x, y))

    for x in range(w):
        push(x, 0)
        push(x, h - 1)
    for y in range(h):
        push(0, y)
        push(w - 1, y)

    exterior = bytearray(w * h)
    while stack:
        x, y = stack.pop()
        exterior[y * w + x] = 1
        push(x + 1, y)
        push(x - 1, y)
        push(x, y + 1)
        push(x, y - 1)

    out = im.copy()
    opx = out.load()
    cleared = 0
    for y in range(h):
        row = y * w
        for x in range(w):
            if exterior[row + x]:
                r, g, b, a = opx[x, y]
                if a != 0:
                    cleared += 1
                opx[x, y] = (r, g, b, 0)

    # Feather alpha along the cut so the white shield rim isn't jagged in the tray.
    alpha = out.getchannel("A")
    soft = alpha.filter(ImageFilter.GaussianBlur(radius=0.8))
    # Keep fully-opaque shield pixels opaque; only soften the rim transition.
    soft_px = soft.load()
    a_px = alpha.load()
    for y in range(h):
        for x in range(w):
            oa = a_px[x, y]
            sa = soft_px[x, y]
            if oa == 0:
                continue
            # If original was fully opaque inside, keep it; only blend near edge.
            if oa == 255 and sa > 240:
                soft_px[x, y] = 255
            elif oa > 0:
                soft_px[x, y] = max(oa, sa) if sa < oa else sa
    out.putalpha(soft)

    print(f"cleared exterior pixels: {cleared}")
    return out


def fit_square(im: Image.Image, size: int, pad_ratio: int = 6) -> Image.Image:
    """Center content in a transparent square with a small safe pad."""
    bbox = im.getbbox()
    if not bbox:
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        return canvas
    cropped = im.crop(bbox)
    pad = max(2, size // pad_ratio)
    avail = size - pad * 2
    cw, ch = cropped.size
    scale = min(avail / cw, avail / ch)
    nw = max(1, int(round(cw * scale)))
    nh = max(1, int(round(ch * scale)))
    resized = cropped.resize((nw, nh), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(resized, ((size - nw) // 2, (size - nh) // 2), resized)
    return canvas


def adaptive_foreground(im: Image.Image, size: int = 432) -> Image.Image:
    """
    Android adaptive icon foreground: content in the center 66% safe zone.
    """
    bbox = im.getbbox()
    if not bbox:
        return Image.new("RGBA", (size, size), (0, 0, 0, 0))
    cropped = im.crop(bbox)
    safe = int(size * 0.66)
    cw, ch = cropped.size
    scale = min(safe / cw, safe / ch)
    nw = max(1, int(round(cw * scale)))
    nh = max(1, int(round(ch * scale)))
    resized = cropped.resize((nw, nh), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(resized, ((size - nw) // 2, (size - nh) // 2), resized)
    return canvas


def main() -> None:
    print("source:", SRC)
    raw = Image.open(SRC).convert("RGBA")
    analyze(raw)
    cleaned = flood_clear_exterior(raw)
    analyze(cleaned)

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    master = fit_square(cleaned, 1024, pad_ratio=12)
    master.save(ICON_MASTER, "PNG", optimize=True)
    print("wrote", ICON_MASTER)

    fg = adaptive_foreground(cleaned, 432)
    fg.save(ICON_FG, "PNG", optimize=True)
    print("wrote", ICON_FG)

    # Docs / README logo (transparent)
    docs = fit_square(cleaned, 1024, pad_ratio=14)
    DOCS_LOGO.parent.mkdir(parents=True, exist_ok=True)
    docs.save(DOCS_LOGO, "PNG", optimize=True)
    print("wrote", DOCS_LOGO)

    # Android legacy density sizes (mdpi..xxxhdpi) + round
    sizes = {
        "mdpi": 48,
        "hdpi": 72,
        "xhdpi": 96,
        "xxhdpi": 144,
        "xxxhdpi": 192,
    }
    for name, px in sizes.items():
        p = OUT_DIR / f"XRTAK-icon-{name}.png"
        fit_square(cleaned, px, pad_ratio=10).save(p, "PNG", optimize=True)
        print("wrote", p)

    # Unity PlayerSettings sizes we need to assign
    for px in (36, 48, 72, 96, 144, 192, 108, 162, 216, 324, 432):
        p = OUT_DIR / f"XRTAK-android-{px}.png"
        fit_square(cleaned, px, pad_ratio=10).save(p, "PNG", optimize=True)

    print("done")


if __name__ == "__main__":
    main()
