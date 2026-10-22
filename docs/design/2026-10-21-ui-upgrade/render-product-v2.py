from pathlib import Path
import hashlib
import json

from PIL import Image, ImageDraw, ImageFilter, ImageFont


OUT = Path(__file__).resolve().parent
ROOT = OUT.parents[2]
REFERENCE = Path(
    r"D:\Users\666\Temp\codex-clipboard-f6a811b1-50bc-4173-bd0d-9d5e5924ad51.png"
)
SCALE = 2
WIDTH, HEIGHT = 1200, 850
COLORS = {
    "canvas": "#DDDFE1",
    "surface": "#F5F6F7",
    "white": "#FFFFFF",
    "ink": "#202124",
    "secondary": "#646B75",
    "muted": "#69717C",
    "line": "#E9EBEE",
    "blue": "#2875CB",
    "selected": "#EDF4FD",
    "green": "#26745B",
}
image = Image.new("RGB", (WIDTH * SCALE, HEIGHT * SCALE), COLORS["canvas"])
draw = ImageDraw.Draw(image)
font_cache = {}


def font(size, weight="regular", symbols=False):
    key = (size, weight, symbols)
    if key not in font_cache:
        name = "SegoeIcons.ttf" if symbols else {
            "regular": "msyh.ttc",
            "bold": "msyhbd.ttc",
        }[weight]
        font_cache[key] = ImageFont.truetype(
            str(Path("C:/Windows/Fonts") / name), round(size * SCALE)
        )
    return font_cache[key]


def box(bounds, fill, radius=0, stroke=None, width=1):
    xy = tuple(round(a * SCALE) for a in bounds)
    if radius:
        draw.rounded_rectangle(
            xy, round(radius * SCALE), fill, stroke, round(width * SCALE)
        )
    else:
        draw.rectangle(xy, fill, stroke, round(width * SCALE))


def text(x, y, value, size=13, color=None, bold=False, anchor="lt"):
    draw.text(
        (round(x * SCALE), round(y * SCALE)),
        value,
        font=font(size, "bold" if bold else "regular"),
        fill=color or COLORS["ink"],
        anchor=anchor,
    )


def icon(x, y, code, size=17, color=None):
    draw.text(
        (round(x * SCALE), round(y * SCALE)),
        chr(code),
        font=font(size, symbols=True),
        fill=color or COLORS["secondary"],
        anchor="mm",
    )


def line(x1, y1, x2, y2, color=None):
    draw.line(
        (x1 * SCALE, y1 * SCALE, x2 * SCALE, y2 * SCALE),
        fill=color or COLORS["line"],
        width=SCALE,
    )


def pill(x, y, label, width, fill, color, height=25, size=11):
    box((x, y, x + width, y + height), fill, height / 2)
    text(x + width / 2, y + (height - size) / 2 - 1, label, size, color, anchor="mt")


def key(x, y, label, width):
    box((x, y, x + width, y + 20), "#FFFFFF", 5, "#E4E7EB")
    text(x + width / 2, y + 4, label, 10, COLORS["secondary"], anchor="mt")


shadow = Image.new("RGBA", image.size)
shadow_draw = ImageDraw.Draw(shadow)
shadow_draw.rounded_rectangle(
    (38 * SCALE, 41 * SCALE, 1162 * SCALE, 817 * SCALE),
    26 * SCALE,
    fill=(30, 35, 45, 12),
)
shadow = shadow.filter(ImageFilter.GaussianBlur(15 * SCALE))
image = Image.alpha_composite(image.convert("RGBA"), shadow).convert("RGB")
draw = ImageDraw.Draw(image)
box((38, 34, 1162, 810), COLORS["surface"], 25)

# The app's real brand asset is retained; all clipboard rows are sample content.
logo = Image.open(ROOT / "src/ClipboardManager.Windows/Assets/AppIcon.png").convert("RGBA")
logo.thumbnail((29 * SCALE, 29 * SCALE), Image.Resampling.LANCZOS)
image.paste(logo, (66 * SCALE, 64 * SCALE), logo)
text(106, 67, "剪切板", 18, bold=True)
for x, label, active in [
    (368, "历史", True),
    (446, "收藏", False),
    (524, "待办", False),
    (602, "计算", False),
    (680, "划词", False),
]:
    if active:
        box((x, 61, x + 64, 97), COLORS["ink"], 18)
    text(x + 32, 71, label, 13, "#FFFFFF" if active else COLORS["secondary"], anchor="mt")
for x, code in [(966, 0xE769), (1015, 0xE718), (1064, 0xE713), (1113, 0xE8BB)]:
    box((x - 18, 61, x + 18, 97), COLORS["white"], 18, COLORS["line"])
    icon(x, 79, code, 15)

text(66, 130, "历史记录", 28, bold=True)
pill(201, 136, "监听中", 65, "#E3F3EC", COLORS["green"], 24, 10.5)
text(67, 171, "复制过的内容，随时找回。", 12.5, COLORS["secondary"])
box((726, 131, 1134, 177), COLORS["white"], 23, "#E6E8EB")
icon(750, 154, 0xE721, 17)
text(772, 146, "搜索内容或来源应用", 13, COLORS["muted"])
key(1064, 144, "Ctrl F", 48)

# Search scope, content type and persistent-state filters remain distinct.
box((66, 204, 326, 239), "#EDEFF1", 18)
for x, label, active in [(71, "全部", True), (135, "文本", False), (199, "图片", False), (263, "文件", False)]:
    if active:
        box((x, 208, x + 58, 235), COLORS["white"], 14)
    text(x + 29, 216, label, 11, COLORS["ink"] if active else COLORS["secondary"], anchor="mt")
box((340, 205, 422, 239), COLORS["white"], 17, COLORS["line"])
icon(359, 222, 0xE718, 12)
text(374, 215, "置顶", 11)
text(1133, 215, "本机空间", 11.5, COLORS["secondary"], anchor="rt")

# Two different content surfaces: navigation list on the left, reading on the right.
box((66, 253, 522, 741), COLORS["white"], 19, "#E5E8EB")
box((537, 253, 1134, 741), COLORS["white"], 19, "#E5E8EB")
text(86, 275, "最近复制", 13, bold=True)
text(156, 277, "今天", 10.5, COLORS["secondary"])
text(464, 277, "最新优先", 10.5, COLORS["secondary"], anchor="rt")
icon(490, 284, 0xE8CB, 12)
line(86, 302, 502, 302)

rows = [
    ("下一版界面调整", "搜索、内容预览与使用流程", "记事本", "刚刚", 0xE8A5, "text"),
    ("产品界面参考.png", "4096 × 3072", "图片", "3 分钟前", 0xEB9F, "image"),
    ("中国传统配色", "colors.xiaoxiaodong.ai", "浏览器", "8 分钟前", 0xE71B, "link"),
    ("本周工作记录.xlsx", "文件路径引用", "资源管理器", "12 分钟前", 0xE8A5, "file"),
    ("#2875CB", "产品主色", "文本", "16 分钟前", 0xE790, "color"),
    ("周四讨论交互方案", "先确认内容结构，再确认动效", "记事本", "昨天", 0xE8A5, "text"),
]
for i, (title, subtitle, source, when, glyph, kind) in enumerate(rows):
    y = 311 + i * 69
    selected = i == 0
    if selected:
        box((75, y, 513, y + 66), COLORS["selected"], 11)
        draw.ellipse((493 * SCALE, (y + 14) * SCALE, 499 * SCALE, (y + 20) * SCALE), fill=COLORS["blue"])
    if kind == "image":
        thumb = Image.open(REFERENCE).convert("RGB")
        thumb.thumbnail((42 * SCALE, 42 * SCALE), Image.Resampling.LANCZOS)
        box((87, y + 11, 127, y + 51), "#F3F4F6", 8)
        image.paste(thumb, (87 * SCALE, round((y + 15) * SCALE)))
    elif kind == "color":
        box((87, y + 12, 123, y + 48), COLORS["blue"], 10)
    else:
        bg, fg = ("#E1EDFC", COLORS["blue"]) if selected else ("#F0F2F5", COLORS["secondary"])
        if kind == "file":
            bg, fg = "#E6F3EC", "#26745B"
        box((87, y + 11, 123, y + 47), bg, 10)
        icon(105, y + 29, glyph, 17, fg)
    text(138, y + 9, title, 13, COLORS["ink"], True)
    text(138, y + 30, subtitle, 11.5, COLORS["secondary"])
    text(138, y + 49, source, 11, COLORS["muted"])
    text(491, y + 49, when, 11, COLORS["muted"], anchor="rt")
    if not selected and i < 5:
        line(138, y + 67, 495, y + 67)

text(560, 275, "内容预览", 13, bold=True)
pill(637, 272, "文本", 44, "#F1F3F5", COLORS["secondary"], 24, 10)
for x, code in [(1029, 0xE734), (1068, 0xE718), (1107, 0xE712)]:
    icon(x, 284, code, 16)
line(560, 302, 1111, 302)
text(565, 333, "下一版界面调整", 23, bold=True)
pill(565, 374, "纯文本", 58, "#EEF4FC", "#2664A5", 23, 10)
text(635, 380, "保留原始内容", 10.5, COLORS["secondary"])

body_lines = [
    "让复制的内容，更快回到工作里。",
    "",
    "这次调整，先把三件事做好：",
    "1. 搜索始终容易找到，不被其他工具打断。",
    "2. 选中后看清完整内容，再决定是否使用。",
    "3. 复制、收藏和置顶，都有清晰的状态反馈。",
    "",
    "视觉保持干净，操作保持直接。",
]
for i, value in enumerate(body_lines):
    if value:
        text(565, 427 + i * 25, value, 14, COLORS["ink"] if i == 0 else "#535B65")
body = "\n".join(body_lines)
text(565, 653, f"记事本    今天 14:32    {len(body)} 字符", 10.5, COLORS["muted"])
line(560, 680, 1111, 680)
box((560, 696, 694, 728), "#F5F6F8", 16)
icon(577, 712, 0xE8C8, 13)
text(592, 705, "仅复制", 10.5)
text(678, 706, "Ctrl C", 9, COLORS["secondary"], anchor="rt")
box((896, 692, 1111, 730), COLORS["ink"], 19)
text(917, 704, "复制并收起", 12, "#FFFFFF", True)
text(1092, 705, "Enter", 10.5, "#DBDEE3", anchor="rt")

icon(79, 773, 0xE72E, 12)
text(94, 767, "本地存储", 10.5, COLORS["secondary"])
text(189, 767, "自动粘贴关闭", 10.5, COLORS["secondary"])
text(492, 767, "↑ ↓  选择", 10.5, COLORS["secondary"])
text(588, 767, "Space  预览", 10.5, COLORS["secondary"])
text(711, 767, "Ctrl Enter  尝试粘贴", 10.5, COLORS["secondary"])
text(1134, 767, "Esc  收起", 10.5, COLORS["secondary"], anchor="rt")
text(1160, 829, "主页设计稿 v2 · 展开模式 · 示例内容", 9, "#626973", anchor="rt")

target = OUT / "homepage-product-v2.png"
image.save(target)
metadata = {
    "file": target.name,
    "pixels": image.size,
    "canvas_logical_size": [WIDTH, HEIGHT],
    "app_logical_size": [1124, 776],
    "mode": "expanded desktop concept",
    "status": "static mockup; not implemented or interaction tested",
    "direction": "neutral white and grey, blue interaction accent, dark primary controls; no paper texture",
    "reference": REFERENCE.name,
    "sample_data": True,
    "renderer": "local precise typography and layout",
    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
}
(OUT / "product-v2-metadata.json").write_text(
    json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
)
print(json.dumps(metadata, ensure_ascii=False))
