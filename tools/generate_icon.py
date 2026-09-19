from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "RonPatcher" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)

SIZE = 1024
SCALE = 4
W = SIZE * SCALE


def s(value: float) -> int:
    return round(value * SCALE)


def lerp(a: int, b: int, t: float) -> int:
    return round(a + (b - a) * t)


canvas = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gradient = Image.new("RGBA", (W, W), (0, 0, 0, 0))
pixels = gradient.load()
start = (18, 184, 255)
middle = (57, 118, 246)
end = (115, 87, 232)
for y in range(W):
    t = y / (W - 1)
    if t < 0.52:
        q = t / 0.52
        color = tuple(lerp(start[i], middle[i], q) for i in range(3))
    else:
        q = (t - 0.52) / 0.48
        color = tuple(lerp(middle[i], end[i], q) for i in range(3))
    for x in range(W):
        side = x / (W - 1)
        lift = round(12 * (1 - side))
        pixels[x, y] = tuple(min(255, c + lift) for c in color) + (255,)

mask = Image.new("L", (W, W), 0)
ImageDraw.Draw(mask).rounded_rectangle((s(64), s(64), s(960), s(960)), radius=s(224), fill=255)
canvas.alpha_composite(Image.composite(gradient, Image.new("RGBA", (W, W)), mask))

draw = ImageDraw.Draw(canvas)
draw.rounded_rectangle((s(190), s(190), s(834), s(834)), radius=s(190), fill=(8, 24, 67, 54))
draw.rounded_rectangle((s(244), s(244), s(780), s(780)), radius=s(158), fill=(255, 255, 255, 31), outline=(255, 255, 255, 48), width=s(3))

shadow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
shadow_draw = ImageDraw.Draw(shadow)
shadow_draw.line([(s(340), s(417)), (s(650), s(417))], fill=(10, 28, 83, 100), width=s(86))
shadow_draw.line([(s(684), s(606)), (s(374), s(606))], fill=(10, 28, 83, 100), width=s(86))
shadow = shadow.filter(ImageFilter.GaussianBlur(s(22)))
canvas.alpha_composite(shadow)

draw = ImageDraw.Draw(canvas)
white = (255, 255, 255, 255)
cyan = (144, 239, 255, 255)
draw.line([(s(326), s(395)), (s(675), s(395))], fill=white, width=s(82))
draw.polygon([(s(675), s(322)), (s(772), s(395)), (s(675), s(468))], fill=white)
draw.ellipse((s(275), s(344), s(377), s(446)), fill=(30, 92, 228, 255), outline=white, width=s(20))

draw.line([(s(698), s(619)), (s(349), s(619))], fill=cyan, width=s(82))
draw.polygon([(s(349), s(546)), (s(252), s(619)), (s(349), s(692))], fill=cyan)
draw.ellipse((s(647), s(568), s(749), s(670)), fill=(43, 102, 235, 255), outline=cyan, width=s(20))

icon = canvas.resize((SIZE, SIZE), Image.Resampling.LANCZOS)
icon.save(ASSETS / "AppIcon.png", optimize=True)
icon.save(
    ASSETS / "AppIcon.ico",
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print(ASSETS / "AppIcon.ico")
