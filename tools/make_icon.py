"""アプリのアイコン（Assets/Hayabumi.ico）を作る。封筒に速さを表す線を重ねた図柄。"""
import sys
from PIL import Image, ImageDraw

def draw(size: int) -> Image.Image:
    s = size * 4  # 高解像度で描いて縮小（なめらかにするため）
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = s * 0.2
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=(47, 111, 222, 255))
    # 封筒
    left, top, right, bottom = s * 0.30, s * 0.30, s * 0.86, s * 0.70
    w = max(2, int(s * 0.055))
    d.rounded_rectangle([left, top, right, bottom], radius=s * 0.04, outline=(255, 255, 255, 255), width=w)
    d.line([(left + w / 2, top + w / 2), ((left + right) / 2, (top + bottom) / 2 + s * 0.02), (right - w / 2, top + w / 2)],
           fill=(255, 255, 255, 255), width=w, joint="curve")
    # 速さの線
    for i, y in enumerate((0.40, 0.50, 0.60)):
        length = (0.16, 0.12, 0.08)[i]
        d.line([(s * (0.26 - length), s * y), (s * 0.22, s * y)], fill=(255, 255, 255, 230), width=w)
    return img.resize((size, size), Image.LANCZOS)

if __name__ == "__main__":
    out = sys.argv[1]
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [draw(n) for n in sizes]
    images[-1].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])
    draw(256).save(out.replace(".ico", ".png"))
