# /// script
# requires-python = ">=3.10"
# dependencies = ["Pillow"]
# ///
"""Pet v2 "夹夹": a sheet of paper with a dog-ear, ruled lines and a terracotta clip.

Pixel art is defined in code on a 28x20 logical grid and exported at exactly 2x
(56x40), the size of CollapsedPetWindow, so frames are never rescaled by a
non-integer factor and the sprite is never squashed. File names and frame counts
match the current Assets/Pet set (idle 8, sleep 6, hop 6), so the set is drop-in;
carry-0/1 is optional (drag-armed feedback).

Usage: python scripts/build-pet-assets.py   (writes Assets/Pet frames and artifacts/pet/contact-sheet.png)
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
FRAMES_OUT = ROOT / "src" / "ClipboardManager.Windows" / "Assets" / "Pet"
SHEET_OUT = ROOT / "artifacts" / "pet" / "contact-sheet.png"
GW, GH, SCALE = 28, 20, 2
INK = (43, 38, 34, 255)
PAPER = (252, 250, 244, 255)
SHADE = (232, 224, 210, 255)
LINE = (214, 203, 184, 255)
CLIP = (217, 119, 87, 255)       # brand terracotta #D97757
CLIPD = (184, 92, 56, 255)       # #B85C38
BLUSH = (240, 160, 140, 255)
WHITE = (255, 255, 255, 255)

W, X0, FEET_Y = 18, 5, 17        # body cols 5..22, feet rows 17..18, shadow row 19
CUTS = (3, 1, 1)                 # rounded-corner pixel cuts, first rows
EAR = 4                          # dog-ear size at top-right


def draw(body_h=13, dy=0, eyes="open", mouth=None, clip_dx=0, clip_dy=0,
         feet_dx=(0, 0), shadow=64, z=None):
    img = Image.new("RGBA", (GW, GH), (0, 0, 0, 0))
    px = img.load()

    def put(x, y, c):
        if 0 <= x < GW and 0 <= y < GH:
            px[x, y] = c

    fy = FEET_Y - dy
    bottom = fy - 1
    top = bottom - body_h + 1

    sw = max(4, W - 4 - 2 * dy)
    sx = X0 + (W - sw) // 2
    for x in range(sx, sx + sw):
        put(x, GH - 1, (0, 0, 0, max(0, shadow - 14 * dy)))

    for k, fx in enumerate((X0 + 3, X0 + 12)):
        ox = fx + feet_dx[k]
        for x in range(ox, ox + 3):
            put(x, fy, INK if x in (ox, ox + 2) else SHADE)
            put(x, fy + 1, INK)

    def inside(x, y):
        ry, rx = y - top, x - X0
        if not (0 <= rx < W and 0 <= ry < body_h):
            return False
        if ry < EAR and rx >= W - EAR + ry:          # dog-ear cut
            return False
        for edge, cut_rows in ((ry, CUTS), (body_h - 1 - ry, CUTS)):
            if edge < len(cut_rows) and (rx < cut_rows[edge] or W - 1 - rx < cut_rows[edge]):
                return False
        return True

    for y in range(top, bottom + 1):
        for x in range(X0, X0 + W):
            if not inside(x, y):
                continue
            ry, rx = y - top, x - X0
            edge = any(not inside(x + a, y + b) for a, b in ((1, 0), (-1, 0), (0, 1), (0, -1)))
            if edge:
                put(x, y, INK)
            elif 1 <= ry <= EAR - 1 and rx >= W - EAR - 1 and rx <= W - EAR + ry - 2:
                put(x, y, SHADE)                       # folded flap
            elif bottom - y <= 2:
                put(x, y, SHADE)
            else:
                put(x, y, PAPER)
    ly = top + 9
    if ly < bottom:
        for x in range(X0 + 4, X0 + W - 4):
            put(x, ly, LINE)

    for e in (X0 + 5, X0 + 11):
        if eyes == "open":
            for x in (e, e + 1):
                for y in (top + 4, top + 5, top + 6):
                    put(x, y, INK)
        elif eyes == "closed":
            put(e, top + 5, INK); put(e + 1, top + 5, INK)
        elif eyes == "happy":
            put(e, top + 4, INK); put(e + 1, top + 4, INK)
            put(e - 1, top + 5, INK); put(e + 2, top + 5, INK)
        elif eyes == "wide":
            for x in (e, e + 1, e + 2):
                for y in (top + 4, top + 5, top + 6):
                    put(x, y, INK)
            put(e + 1, top + 5, WHITE)
    for bx in (X0 + 3, X0 + 13):
        put(bx, top + 7, BLUSH); put(bx + 1, top + 7, BLUSH)
    if mouth == "open":
        for x in (X0 + 8, X0 + 9):
            put(x, top + 7, INK); put(x, top + 8, INK)
    elif eyes != "closed":
        put(X0 + 8, top + 7, INK); put(X0 + 9, top + 7, INK)

    cx, cy = X0 + 3 + clip_dx, top - 2 + clip_dy
    for r in range(4):
        for c in range(6):
            border = r in (0, 3) or c in (0, 5)
            put(cx + c, cy + r, INK if border else (CLIPD if r == 2 else CLIP))

    if z is not None:
        zx, zy, alpha = z
        for x, y in ((0, 0), (1, 0), (2, 0), (3, 0), (2, 1), (1, 2), (0, 3), (1, 3), (2, 3), (3, 3)):
            put(zx + x, zy + y, (CLIP[0], CLIP[1], CLIP[2], alpha))
    return img


def build():
    rows = {}
    idle = [dict(body_h=13), dict(body_h=12), dict(body_h=12), dict(body_h=13),
            dict(eyes="closed"), dict(eyes="closed"),
            dict(clip_dx=-1), dict(clip_dx=1)]
    rows["idle"] = [draw(**p) for p in idle]
    z_alpha = (140, 255, 255, 255, 180, 90)
    sleep_h = (11, 11, 12, 12, 11, 11)
    rows["sleep"] = [draw(body_h=sleep_h[i], eyes="closed", clip_dx=-1, clip_dy=1,
                          z=(24, 8 - i, z_alpha[i])) for i in range(6)]
    hop_dy = (0, 1, 2, 2, 1, 0)
    hop_h = (12, 13, 13, 13, 13, 12)
    rows["hop"] = [draw(body_h=hop_h[i], dy=hop_dy[i],
                        eyes="happy" if 1 <= i <= 4 else "open",
                        mouth="open" if i in (2, 3) else None) for i in range(6)]
    rows["carry"] = [draw(dy=2, eyes="wide", mouth="open", feet_dx=(-1, 1)),
                     draw(dy=2, eyes="wide", mouth="open", feet_dx=(1, -1))]
    return rows


def main():
    rows = build()
    FRAMES_OUT.mkdir(parents=True, exist_ok=True)
    for name, frames in rows.items():
        for i, f in enumerate(frames):
            f.resize((GW * SCALE, GH * SCALE), Image.NEAREST).save(FRAMES_OUT / f"{name}-{i}.png")

    zoom, pad = 8, 10
    cw, ch = GW * zoom, GH * zoom
    cols = max(len(f) for f in rows.values())
    sheet = Image.new("RGB", (cols * (cw + pad) + pad, len(rows) * (ch + pad) + pad), (250, 249, 245))
    y = pad
    for name, frames in rows.items():
        x = pad
        for f in frames:
            big = f.resize((cw, ch), Image.NEAREST)
            sheet.paste(big, (x, y), big)
            x += cw + pad
        y += ch + pad
    SHEET_OUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(SHEET_OUT)
    print({k: len(v) for k, v in rows.items()})


if __name__ == "__main__":
    main()
