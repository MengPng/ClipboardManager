from pathlib import Path
import hashlib
import json

from PIL import Image, ImageDraw, ImageFilter, ImageFont


OUT = Path(__file__).resolve().parent
ROOT = OUT.parents[2]
SCALE = 2
WIDTH, HEIGHT = 340, 540
C = {
    "backdrop": "#DCE0E4",
    "surface": "#F7F8F9",
    "white": "#FFFFFF",
    "ink": "#202124",
    "secondary": "#626A75",
    "muted": "#727B86",
    "line": "#E7EAEE",
    "blue": "#2875CB",
    "blue_soft": "#EAF3FE",
    "green": "#26745B",
}
image = Image.new("RGB", (WIDTH * SCALE, HEIGHT * SCALE), C["backdrop"])
draw = ImageDraw.Draw(image)
fonts = {}


def font(size, bold=False, symbol=False):
    key = (size, bold, symbol)
    if key not in fonts:
        filename = "SegoeIcons.ttf" if symbol else "msyhbd.ttc" if bold else "msyh.ttc"
        fonts[key] = ImageFont.truetype(
            str(Path("C:/Windows/Fonts") / filename), round(size * SCALE)
        )
    return fonts[key]


def box(bounds, fill, radius=0, stroke=None, width=1):
    bounds = tuple(round(value * SCALE) for value in bounds)
    if radius:
        draw.rounded_rectangle(
            bounds,
            radius=round(radius * SCALE),
            fill=fill,
            outline=stroke,
            width=max(1, round(width * SCALE)),
        )
    else:
        draw.rectangle(bounds, fill=fill, outline=stroke, width=max(1, round(width * SCALE)))


def line(x1, y1, x2, y2, color=None):
    draw.line(
        (x1 * SCALE, y1 * SCALE, x2 * SCALE, y2 * SCALE),
        fill=color or C["line"],
        width=SCALE,
    )


def text(x, y, value, size=13, color=None, bold=False, anchor="lt"):
    draw.text(
        (round(x * SCALE), round(y * SCALE)),
        value,
        fill=color or C["ink"],
        font=font(size, bold),
        anchor=anchor,
    )


def icon(x, y, code, size=15, color=None):
    draw.text(
        (round(x * SCALE), round(y * SCALE)),
        chr(code),
        fill=color or C["secondary"],
        font=font(size, symbol=True),
        anchor="mm",
    )


def key(x, y, label, width):
    box((x, y, x + width, y + 16), C["white"], 4, "#E1E5EA")
    text(x + width / 2, y + 2, label, 8.3, C["secondary"], anchor="mt")


# The backdrop frames the real 340 x 540 DIP window; it is not part of the product UI.
shadow = Image.new("RGBA", image.size)
shadow_draw = ImageDraw.Draw(shadow)
shadow_draw.rounded_rectangle(
    (7 * SCALE, 8 * SCALE, 333 * SCALE, 532 * SCALE),
    12 * SCALE,
    fill=(24, 32, 42, 34),
)
shadow = shadow.filter(ImageFilter.GaussianBlur(10 * SCALE))
image = Image.alpha_composite(image.convert("RGBA"), shadow).convert("RGB")
draw = ImageDraw.Draw(image)
box((8, 7, 332, 532), C["surface"], 12)

# The current product icon remains, but the title bar itself is quiet.
logo = Image.open(ROOT / "src/ClipboardManager.Windows/Assets/AppIcon.png").convert("RGBA")
logo.thumbnail((16 * SCALE, 16 * SCALE), Image.Resampling.LANCZOS)
image.paste(logo, (18 * SCALE, 19 * SCALE), logo)
text(42, 18, "剪切板", 13.2, bold=True)
draw.ellipse((107 * SCALE, 23 * SCALE, 112 * SCALE, 28 * SCALE), fill=C["green"])
text(116, 18, "监听中", 10.2, C["green"])
for x, code in [(236, 0xE769), (263, 0xE718), (290, 0xE713), (316, 0xE8BB)]:
    icon(x, 26, code, 14)
line(8, 44, 332, 44)

# In a narrow window the navigation must stay legible but visually recede.
tabs = [(15, "历史", True), (78, "待办", False), (141, "配置", False), (204, "计算", False), (267, "划词", False)]
for x, label, selected in tabs:
    if selected:
        box((x, 53, x + 48, 78), C["ink"], 12)
    text(x + 24, 59, label, 11, C["white"] if selected else C["secondary"], selected, "mt")

box((16, 89, 324, 124), C["white"], 9, "#E0E4E9")
icon(34, 106, 0xE721, 14)
text(51, 98, "搜索内容或来源应用", 11.2, C["muted"])
key(279, 98, "Ctrl F", 35)

# A single filter track avoids separate boxed controls competing for attention.
box((16, 134, 324, 160), "#ECEEF1", 13)
for x, label, selected in [(21, "全部", True), (85, "文本", False), (149, "图片", False), (213, "文件", False)]:
    if selected:
        box((x, 137, x + 46, 157), C["white"], 10)
    text(x + 23, 141, label, 9.8, C["ink"] if selected else C["secondary"], anchor="mt")
line(270, 140, 270, 154, "#D7DCE2")
icon(291, 147, 0xE718, 11)
text(306, 141, "置顶", 9.5, anchor="mt")

text(17, 174, "今天", 11.5, bold=True)
text(48, 175, "10月21日", 10, C["secondary"])
text(324, 175, "5 条", 10, C["secondary"], anchor="rt")

# No thumbnails, source labels, or duplicated metadata. The text itself is the item.
rows = [
    ("下一版界面调整", "搜索、预览与使用流程", "刚刚"),
    ("产品界面参考", "4096 × 3072", "3 分钟前"),
    ("中国传统配色", "colors.xiaoxiaodong.ai", "8 分钟前"),
    ("本周工作记录.xlsx", "文件路径引用", "12 分钟前"),
    ("先把重要的事情做好。", "可从剪切板直接找回", "18 分钟前"),
]
for index, (title, preview, when) in enumerate(rows):
    y = 191 + index * 53
    selected = index == 0
    if selected:
        box((13, y - 4, 327, y + 44), C["blue_soft"], 8)
        line(19, y + 11, 19, y + 31, C["blue"])
        draw.ellipse((311 * SCALE, (y + 11) * SCALE, 317 * SCALE, (y + 17) * SCALE), fill=C["blue"])
    text(31 if selected else 17, y + 2, title, 11.8, C["ink"], True)
    text(31 if selected else 17, y + 22, preview, 10.2, C["secondary"])
    text(306, y + 22, when, 9.6, C["muted"], anchor="rt")
    if index < len(rows) - 1:
        line(17, y + 50, 306, y + 50)

# The action area continues the same surface instead of becoming a billboard.
line(16, 456, 324, 456)
icon(28, 477, 0xE8C8, 14, C["secondary"])
text(43, 468, "已选 1 条", 10.5, C["secondary"])
box((237, 462, 324, 494), C["ink"], 16)
text(262, 470, "复制", 10.8, "#FFFFFF", True)
text(309, 471, "Enter", 8.7, "#D8DDE4", anchor="rt")
text(16, 509, "Ctrl + Enter  尝试粘贴", 9.2, C["secondary"])
text(324, 509, "Space  预览", 9.2, C["secondary"], anchor="rt")
text(16, 523, "本地存储", 8.7, C["muted"])
text(324, 523, "Esc  收起", 8.7, C["muted"], anchor="rt")

target = OUT / "homepage-compact-v4.png"
image.save(target)
metadata = {
    "file": target.name,
    "pixels": image.size,
    "window_logical_size": [WIDTH, HEIGHT],
    "minimum_logical_size": [340, 450],
    "mode": "default compact home; static mockup only",
    "status": "not implemented or interaction tested",
    "direction": "continuous compact reading surface; no row thumbnails, source line, or full-width action banner",
    "sample_data": True,
    "renderer": "local precise typography and layout",
    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
}
(OUT / "compact-v4-metadata.json").write_text(
    json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
)
print(json.dumps(metadata, ensure_ascii=False))
