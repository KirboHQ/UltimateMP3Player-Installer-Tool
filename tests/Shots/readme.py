# The README's pictures (assets/readme/<lang>), made from the screens Shots draws at twice the size, in the look of the music
# app's README (its tools/readme-shots/compose.py): the window with its corners, buttons and shadow, the hero on the brand's purple.
#   SHOTS_SCALE=2 UMP_LANG=en Shots.exe <folder>/en, the same with it, then: python -I tests/Shots/readme.py <folder>
# (needs pillow and numpy)
import os, sys, numpy as np
from PIL import Image, ImageDraw, ImageFilter

FOLDER = sys.argv[1]
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "assets", "readme")
PURPLE = [(0.0, (40, 26, 74)), (0.55, (78, 44, 110)), (1.0, (118, 52, 104))]


def rounded(im, r):
    im = im.convert("RGBA")
    m = Image.new("L", im.size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, im.width - 1, im.height - 1), r, fill=255)
    im.putalpha(Image.fromarray(np.minimum(np.array(im.getchannel("A")), np.array(m))))
    return im


def gradient(w, h, stops):
    # diagonal, top-left to bottom-right
    y, x = np.mgrid[0:h, 0:w]
    t = x / w * 0.6 + y / h * 0.4
    out = np.zeros((h, w, 3))
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        k = np.clip((t - p0) / (p1 - p0), 0, 1)[..., None]
        sel = ((t >= p0) & (t <= p1))[..., None]
        out = np.where(sel, np.array(c0) * (1 - k) + np.array(c1) * k, out)
    return Image.fromarray(out.astype("uint8"), "RGB").convert("RGBA")


def paste_shadow(canvas, card, xy, blur, dy, alpha):
    sh = Image.new("RGBA", (card.width + blur * 4, card.height + blur * 4), (0, 0, 0, 0))
    sh.paste((0, 0, 0, 255), (blur * 2, blur * 2), card.getchannel("A").point(lambda v: v * alpha // 255))
    canvas.alpha_composite(sh.filter(ImageFilter.GaussianBlur(blur)), (xy[0] - blur * 2, xy[1] - blur * 2 + dy))
    canvas.alpha_composite(card, xy)


def window(lang, name, width):
    # Shots draws only what's inside: Windows 11's buttons go on top (46×32 each, 10 px glyphs), as on the real window.
    im = Image.open(os.path.join(FOLDER, lang, "shots", name + ".png")).convert("RGBA")
    s = im.width / 1120
    d = ImageDraw.Draw(im)
    ink, line, g = (255, 255, 255, 215), max(1, round(s)), 5 * s
    for i, glyph in enumerate(("close", "max", "min")):
        cx, cy = im.width - 23 * s - 46 * s * i, 16 * s
        if glyph == "close":
            d.line((cx - g, cy - g, cx + g, cy + g), ink, line)
            d.line((cx - g, cy + g, cx + g, cy - g), ink, line)
        elif glyph == "max":
            d.rounded_rectangle((cx - g, cy - g, cx + g, cy + g), s, outline=ink, width=line)
        else:
            d.line((cx - g, cy, cx + g, cy), ink, line)
    im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
    r = round(8 * width / 1120)
    # the thin light edge of a dark window on Windows 11
    ImageDraw.Draw(im).rounded_rectangle((0, 0, im.width - 1, im.height - 1), r, outline=(255, 255, 255, 34), width=1)
    return rounded(im, r)


def save(im, lang, name):
    path = os.path.join(OUT, lang, name)
    im.save(path, "WEBP", quality=86, method=6)
    print(lang, name, im.size, os.path.getsize(path) // 1024, "KB")


for lang in ("en", "it"):
    os.makedirs(os.path.join(OUT, lang), exist_ok=True)

    # Hero: the first page, on the purple.
    W, H = 1800, 1240
    hero = gradient(W, H, PURPLE)
    card = window(lang, "welcome", 1620)
    paste_shadow(hero, card, ((W - card.width) // 2, (H - card.height) // 2 - 10), blur=30, dy=18, alpha=140)
    save(rounded(hero, 36), lang, "hero.webp")

    # The steps: the window alone, on transparency.
    for name in ("choice", "activate", "sidestore", "done"):
        card = window(lang, name, 1120)
        m = 44
        canvas = Image.new("RGBA", (card.width + 2 * m, card.height + 2 * m + 12), (0, 0, 0, 0))
        paste_shadow(canvas, card, (m, m - 8), blur=22, dy=12, alpha=110)
        save(canvas, lang, name + ".webp")
