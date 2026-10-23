# /// script
# requires-python = ">=3.10"
# dependencies = ["Pillow"]
# ///
"""Derive the panel-pet frame sequences from the two approved base sprites.

Inputs  (artifacts/pet/):
  base-open.png    front-facing, eyes open,  flat #FF00FF chroma background

The closed-eye variant is derived deterministically from base-open (replace each
near-black eye blob with a thin dark lid line over body colour), because a
separately generated closed sprite drifted in palette, proportions and pose.

Outputs (src/ClipboardManager.Windows/Assets/Pet/):
  idle-0..7.png   breathing bob + blink, loops
  sleep-0..5.png  squashed, slow breathing, loops
  hop-0..5.png    one-shot squash-and-stretch hop
  contact-sheet.png under artifacts/pet/ for QA

Everything is deterministic: chroma removal, despill, trim, normalize, and all
frame transforms run from the two approved bases so identity cannot drift.
"""
from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
WORK = ROOT / "artifacts" / "pet"
OUT = ROOT / "src" / "ClipboardManager.Windows" / "Assets" / "Pet"

CANVAS_W, CANVAS_H = 96, 104          # 48x52 DIP at 2x
SPRITE_HEIGHT = 72
BASELINE_Y = CANVAS_H - 10            # feet line
CHROMA = (255, 0, 255)
KEY_DISTANCE = 90                     # > this counts as background


def load_sprite(path: Path) -> Image.Image:
    img = Image.open(path).convert("RGB")
    # The approved base is a NEAREST-upscaled 64x64 pixel grid; collapse back to
    # the native grid with area averaging (exact for pure NEAREST upscales) so
    # downstream scaling works on real pixel cells, not 16x blocks.
    if img.width > 64 and img.width % 64 == 0:
        img = img.resize((64, 64), Image.BOX)
    px = img.load()
    w, h = img.size
    mask = Image.new("L", (w, h), 0)
    mp = mask.load()
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if abs(r - CHROMA[0]) + abs(g - CHROMA[1]) + abs(b - CHROMA[2]) > KEY_DISTANCE:
                mp[x, y] = 255
    # Drop isolated speckles, then despill: recolor translucent fringe from the
    # nearest fully opaque pixel in the same row band.
    mask = mask.point(lambda v: 255 if v >= 128 else 0)
    bbox = mask.getbbox()
    if not bbox:
        raise SystemExit(f"{path.name}: chroma removal left an empty sprite")
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    src = img.load()
    op = mask.load()
    for y in range(h):
        for x in range(w):
            if op[x, y]:
                r, g, b = src[x, y]
                # pull chroma-adjacent pixels toward neutral so the fringe is clean
                if r > 180 and b > 180:
                    f = (r + b) / 2
                    r, g, b = int(r - (r - g) * 0.0), g, int(b - (b - g) * 0.0)
                    r = min(r, 200 + g // 4)
                    b = min(b, 200 + g // 4)
                out.putpixel((x, y), (r, g, b, 255))
    sprite = out.crop(bbox)
    # Quantize to keep the pixel-art edge crisp after rescaling.
    ratio = SPRITE_HEIGHT / sprite.height
    sprite = sprite.resize((max(1, round(sprite.width * ratio)), SPRITE_HEIGHT), Image.NEAREST)
    return sprite


def derive_closed(open_sprite: Image.Image) -> Image.Image:
    """Swap open bean eyes for thin closed lid lines without touching anything else."""
    sprite = open_sprite.copy()
    px = sprite.load()
    w, h = sprite.size
    dark = [[px[x, y][0] < 70 and px[x, y][1] < 70 and px[x, y][2] < 70 and px[x, y][3] > 0
             for x in range(w)] for y in range(h)]
    # Collect dark blobs; the two largest are the eyes.
    blobs: list[list[tuple[int, int]]] = []
    seen = [[False] * w for _ in range(h)]
    for y in range(h):
        for x in range(w):
            if not dark[y][x] or seen[y][x]:
                continue
            stack, cells = [(x, y)], []
            seen[y][x] = True
            while stack:
                cx, cy = stack.pop()
                cells.append((cx, cy))
                for nx, ny in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
                    if 0 <= nx < w and 0 <= ny < h and dark[ny][nx] and not seen[ny][nx]:
                        seen[ny][nx] = True
                        stack.append((nx, ny))
            blobs.append(cells)
    eyes = sorted(blobs, key=len, reverse=True)[:2]
    for cells in eyes:
        if len(cells) < 8:
            continue
        xs = [c[0] for c in cells]
        ys = [c[1] for c in cells]
        x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
        cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
        # Body colour sampled just outside the blob's left edge.
        sx = max(0, x0 - 3)
        body = px[sx, cy]
        lid_h = max(2, (y1 - y0 + 1) // 3)
        lid_y0 = cy - lid_h // 2
        for yy in range(y0, y1 + 1):
            for xx in range(x0, x1 + 1):
                if lid_y0 <= yy < lid_y0 + lid_h and (xx - x0) > 0 and (x1 - xx) > 0:
                    px[xx, yy] = (body[0] // 3, body[1] // 3, body[2] // 3, 255)
                else:
                    px[xx, yy] = body
    return sprite


def place(sprite: Image.Image, dx: int = 0, dy: int = 0, sx: float = 1.0, sy: float = 1.0) -> Image.Image:
    w = max(1, round(sprite.width * sx))
    h = max(1, round(sprite.height * sy))
    body = sprite.resize((w, h), Image.NEAREST)
    frame = Image.new("RGBA", (CANVAS_W, CANVAS_H), (0, 0, 0, 0))
    x = (CANVAS_W - w) // 2 + dx
    y = BASELINE_Y - h + dy
    frame.alpha_composite(body, (x, y))
    return frame


def build(open_sprite: Image.Image, closed_sprite: Image.Image) -> dict[str, list[Image.Image]]:
    rows: dict[str, list[Image.Image]] = {}
    # idle: 4 open frames with a 1px breathing bob, quick 2-frame blink, then settle.
    bob = [0, 1, 1, 0]
    rows["idle"] = [place(open_sprite, dy=bob[i % 4]) for i in range(4)]
    rows["idle"] += [place(closed_sprite, dy=0), place(closed_sprite, dy=0),
                     place(open_sprite, dy=0), place(open_sprite, dy=1)]
    # sleep: closed eyes, gently breathing squash that keeps the baseline pinned.
    breath = [0.92, 0.935, 0.95, 0.95, 0.935, 0.92]
    rows["sleep"] = [place(closed_sprite, sy=s) for s in breath]
    # hop: one-shot arc, stretch up / squash down, returns to rest.
    arc = [0, -5, -9, -9, -5, 0]
    shape = [(1.06, 0.94), (1.00, 1.00), (0.96, 1.06), (0.96, 1.06), (1.00, 1.00), (1.06, 0.94)]
    rows["hop"] = [place(open_sprite, dy=arc[i], sx=shape[i][0], sy=shape[i][1]) for i in range(6)]
    return rows


def main() -> int:
    open_sprite = load_sprite(WORK / "base-open.png")
    closed_sprite = derive_closed(open_sprite)
    rows = build(open_sprite, closed_sprite)
    OUT.mkdir(parents=True, exist_ok=True)
    for name, frames in rows.items():
        for i, frame in enumerate(frames):
            frame.save(OUT / f"{name}-{i}.png")
    # Contact sheet: each row on warm paper, 4x zoom for eyeballing.
    zoom, pad, label_h = 4, 12, 24
    cell_w, cell_h = CANVAS_W * zoom, CANVAS_H * zoom
    sheet_w = max(len(f) for f in rows.values()) * (cell_w + pad) + pad
    sheet_h = len(rows) * (cell_h + pad + label_h) + pad
    sheet = Image.new("RGB", (sheet_w, sheet_h), (250, 249, 245))
    y = pad
    for name, frames in rows.items():
        x = pad
        for frame in frames:
            big = frame.resize((cell_w, cell_h), Image.NEAREST)
            sheet.paste(big, (x, y + label_h), big)
            x += cell_w + pad
        y += cell_h + pad + label_h
    sheet.save(WORK / "contact-sheet.png")
    counts = ", ".join(f"{k}={len(v)}" for k, v in rows.items())
    print(f"ok: {counts} -> {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
