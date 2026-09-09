from pathlib import Path
import shutil
import sys

from PIL import Image


def main() -> int:
    if len(sys.argv) != 3:
        raise SystemExit("usage: build-logo-assets.py <source.png> <assets-dir>")

    source = Path(sys.argv[1]).resolve()
    assets = Path(sys.argv[2]).resolve()
    assets.mkdir(parents=True, exist_ok=True)

    brand_logo = assets / "BrandLogo.png"
    app_icon_png = assets / "AppIcon.png"
    app_icon_ico = assets / "AppIcon.ico"
    shutil.copyfile(source, brand_logo)

    image = Image.open(source).convert("RGBA")
    # The supplied artwork is square: the symbol occupies the upper region and
    # the Chinese wordmark is below it. Keep only the symbol for small OS icons.
    symbol = image.crop((315, 125, 940, 850))

    pixels = []
    for red, green, blue, _ in symbol.getdata():
        distance = 255 - min(red, green, blue)
        alpha = max(0, min(255, (distance - 3) * 18))
        pixels.append((red, green, blue, alpha))
    symbol.putdata(pixels)

    bounds = symbol.getbbox()
    if bounds is None:
        raise RuntimeError("No visible logo pixels found")
    symbol = symbol.crop(bounds)
    symbol.thumbnail((448, 448), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    canvas.alpha_composite(symbol, ((512 - symbol.width) // 2, (512 - symbol.height) // 2))
    canvas.save(app_icon_png, optimize=True)
    canvas.save(app_icon_ico, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    print(f"BRAND_LOGO={brand_logo}")
    print(f"APP_ICON_PNG={app_icon_png}")
    print(f"APP_ICON_ICO={app_icon_ico}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
