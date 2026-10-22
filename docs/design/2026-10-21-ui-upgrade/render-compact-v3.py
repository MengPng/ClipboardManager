from pathlib import Path
import hashlib
import json

from PIL import Image, ImageDraw, ImageFilter, ImageFont


OUT = Path(__file__).resolve().parent
ROOT = OUT.parents[2]
SCALE = 2
WIDTH, HEIGHT = 340, 540
C = {
    "backdrop": "#D9DCE0",
    "surface": "#F6F7F8",
    "white": "#FFFFFF",
    "ink": "#202124",
    "secondary": "#626A75",
    "muted": "#69717C",
    "line": "#E5E8EC",
    "blue": "#2875CB",
    "selected": "#EAF3FE",
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
            bounds, radius=round(radius * SCALE), fill=fill, outline=stroke, width=round(width * SCALE)
        )
    else:
        draw.rectangle(bounds, fill=fill, outline=stroke, width=round(width * SCALE))


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


# A soft backdrop only frames the real 340 x 540 DIP window; it is not part of the app.
shadow = Image.new("RGBA", image.size)
shadow_draw = ImageDraw.Draw(shadow)
shadow_draw.rounded_rectangle(
    (7 * SCALE, 8 * SCALE, 333 * SCALE, 532 * SCALE),
    12 * SCALE,
    fill=(25, 35, 48, 34),
)
shadow = shadow.filter(ImageFilter.GaussianBlur(10 * SCALE))
image = Image.alpha_composite(image.convert("RGBA"), shadow).convert("RGB")
draw = ImageDraw.Draw(image)
box((8, 7, 332, 532), C["surface"], 12)

# Title bar: preserves the existing listening, pause, window pin, settings and hide affordances.
logo = Image.open(ROOT / "src/ClipboardManager.Windows/Assets/AppIcon.png").convert("RGBA")
logo.thumbnail((16 * SCALE, 16 * SCALE), Image.Resampling.LANCZOS)
image.paste(logo, (18 * SCALE, 19 * SCALE), logo)
text(42, 18, "剪切板", 13.2, bold=True)
draw.ellipse((107 * SCALE, 23 * SCALE, 112 * SCALE, 28 * SCALE), fill=C["green"])
text(116, 18, "监听中", 10.2, C["green"])
for x, code in [(236, 0xE769), (263, 0xE718), (290, 0xE713), (316, 0xE8BB)]:
    icon(x, 26, code, 14)
line(8, 44, 332, 44)

# The five existing destinations remain top tabs because a compact side rail would waste width.
tabs = [(15, "历史", True), (78, "待办", False), (141, "配置", False), (204, "计算", False), (267, "划词", False)]
for x, label, selected in tabs:
    if selected:
        box((x, 53, x + 48, 78), C["ink"], 12)
    text(x + 24, 59, label, 11, C["white"] if selected else C["secondary"], selected, "mt")

# Search is the first action inside the active page.
box((16, 89, 324, 124), C["white"], 9, "#E0E4E9")
icon(34, 106, 0xE721, 14)
text(51, 98, "搜索内容或来源应用", 11.2, C["muted"])
key(279, 98, "Ctrl F", 35)

# Filters remain compact. Pinned is an icon state rather than another long word in the row.
box((16, 134, 259, 160), "#EDEFF2", 13)
for x, label, selected in [(20, "全部", True), (78, "文本", False), (136, "图片", False), (194, "文件", False)]:
    if selected:
        box((x, 137, x + 47, 157), C["white"], 10)
    text(x + 23.5, 141, label, 9.8, C["ink"] if selected else C["secondary"], anchor="mt")
box((270, 134, 324, 160), C["white"], 13, "#E0E4E9")
icon(285, 147, 0xE718, 11)
text(298, 141, "置顶", 9.5)

text(17, 174, "今天", 11.5, bold=True)
text(48, 175, "10月21日", 10, C["secondary"])
text(324, 175, "5 条", 10, C["secondary"], anchor="rt")

# Dense grouped rows leave enough room for a fixed primary action.
rows = [
    ("下一版界面调整", "搜索、预览与使用流程", "记事本", "刚刚", 0xE8A5, "#E4F0FF"),
    ("产品界面参考.png", "4096 × 3072", "图片", "3 分钟前", 0xEB9F, "#F0F2F5"),
    ("中国传统配色", "colors.xiaoxiaodong.ai", "浏览器", "8 分钟前", 0xE71B, "#F0F2F5"),
    ("本周工作记录.xlsx", "文件路径引用", "资源管理器", "12 分钟前", 0xE8A5, "#E6F3EC"),
    ("先把重要的事情做好。", "可从剪切板直接找回", "记事本", "18 分钟前", 0xE8A5, "#F0F2F5"),
]
for index, (title, subtitle, source, when, glyph, icon_bg) in enumerate(rows):
    y = 190 + index * 54
    selected = index == 0
    if selected:
        box((13, y - 3, 327, y + 48), C["selected"], 9)
        draw.ellipse((312 * SCALE, (y + 12) * SCALE, 318 * SCALE, (y + 18) * SCALE), fill=C["blue"])
    box((22, y + 7, 50, y + 35), icon_bg, 8)
    icon(36, y + 21, glyph, 14, C["blue"] if selected else "#4E8E76" if index == 3 else C["secondary"])
    text(61, y + 4, title, 11.4, C["ink"], True)
    text(61, y + 22, subtitle, 9.8, C["secondary"])
    text(61, y + 38, source, 9.2, C["muted"])
    text(303, y + 38, when, 9.2, C["muted"], anchor="rt")
    if index < len(rows) - 1:
        line(61, y + 52, 304, y + 52)

# Persistent action zone: it has one action and an explicit keyboard contract.
line(16, 462, 324, 462)
box((16, 474, 324, 510), C["ink"], 9)
icon(35, 492, 0xE8C8, 14, "#FFFFFF")
text(53, 482, "复制并收起", 11.5, "#FFFFFF", True)
key(273, 484, "Enter", 40)
text(16, 516, "Ctrl + Enter  尝试粘贴", 9.2, C["secondary"])
text(324, 516, "Space  预览", 9.2, C["secondary"], anchor="rt")

target = OUT / "homepage-compact-v3.png"
image.save(target)
metadata = {
    "file": target.name,
    "pixels": image.size,
    "window_logical_size": [WIDTH, HEIGHT],
    "minimum_logical_size": [340, 450],
    "mode": "default compact home; static mockup only",
    "status": "not implemented or interaction tested",
    "direction": "neutral product surface, compact hierarchy, blue selection accent, no paper texture",
    "sample_data": True,
    "renderer": "local precise typography and layout",
    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
}
(OUT / "compact-v3-metadata.json").write_text(
    json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
)
print(json.dumps(metadata, ensure_ascii=False))
