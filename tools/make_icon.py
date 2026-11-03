"""生成 SMMS 应用图标：星露谷画风的像素小鸡站在文件夹上。

小鸡严格参照游戏内白鸡 sprite：
奶黄色横椭圆身体、与身体无缝相连的头、三层扇形翘尾、只露脚趾的短腿。
基准网格 64x64，放大用 NEAREST 保持硬边像素感。
"""

from PIL import Image, ImageDraw

# 文件夹：木色 + 深棕描边
OUTLINE = (52, 36, 22, 255)
BACK = (178, 134, 60, 255)
FRONT = (222, 174, 84, 255)
FRONT_LIGHT = (243, 205, 126, 255)
SHADE = (160, 118, 48, 255)

# 小鸡：对齐 sprite 的奶黄调，不是纯白
CHICK = (245, 230, 196, 255)
CHICK_LIGHT = (253, 243, 218, 255)
CHICK_SHADE = (214, 184, 132, 255)
CHICK_DEEP = (186, 152, 100, 255)
COMB = (186, 58, 50, 255)
COMB_LIGHT = (214, 86, 72, 255)
BEAK = (240, 190, 56, 255)
FOOT = (232, 170, 44, 255)
EYE = (48, 32, 20, 255)
TRANSPARENT = (0, 0, 0, 0)

SIZE = 64


def build() -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), TRANSPARENT)
    d = ImageDraw.Draw(img)

    def rect(x0, y0, x1, y1, color):
        d.rectangle([x0, y0, x1 - 1, y1 - 1], fill=color)

    def ellipse(cx, cy, rx, ry, color):
        for y in range(cy - ry, cy + ry + 1):
            for x in range(cx - rx, cx + rx + 1):
                if ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1.08:
                    img.putpixel((x, y), color)

    def disc(cx, cy, r, color):
        ellipse(cx, cy, r, r, color)

    def is_chick(x, y):
        return img.getpixel((x, y))[:3] in (CHICK[:3], CHICK_LIGHT[:3])

    # ================= 文件夹（底座） =================
    rect(5, 35, 25, 42, BACK)
    rect(5, 40, 59, 61, BACK)
    rect(5, 44, 57, 61, FRONT)
    rect(5, 44, 57, 47, FRONT_LIGHT)
    rect(5, 47, 8, 61, FRONT_LIGHT)
    rect(54, 44, 57, 61, SHADE)
    rect(8, 58, 54, 61, SHADE)

    # ================= 小鸡（站在文件夹顶边 y=40 上） =================
    # 尾巴：三层扇形，从身体左后方递进上翘
    rect(17, 26, 24, 33, CHICK_SHADE)
    rect(15, 22, 22, 30, CHICK)
    rect(18, 18, 25, 26, CHICK)
    rect(13, 28, 20, 36, CHICK_SHADE)

    # 身体：横向椭圆，比正圆更贴近 sprite 体型
    ellipse(31, 30, 12, 9, CHICK)

    # 上半受光
    for x in range(19, 44):
        for y in range(21, 27):
            if is_chick(x, y):
                img.putpixel((x, y), CHICK_LIGHT)

    # 腹与下缘阴影
    for x in range(18, 45):
        for y in range(33, 41):
            if is_chick(x, y):
                img.putpixel((x, y), CHICK_SHADE)

    for x in range(24, 40):
        for y in range(37, 41):
            if img.getpixel((x, y))[:3] == CHICK_SHADE[:3]:
                img.putpixel((x, y), CHICK_DEEP)

    # 头：右上，与身体无缝相接（sprite 里没有脖子分界）
    disc(37, 20, 8, CHICK)

    for x in range(31, 44):
        for y in range(12, 17):
            if is_chick(x, y):
                img.putpixel((x, y), CHICK_LIGHT)

    # 鸡冠：三撮小凸起，靠上的那撮更亮
    rect(33, 9, 36, 14, COMB)
    rect(36, 7, 39, 13, COMB_LIGHT)
    rect(39, 10, 42, 14, COMB)

    # 喙：右侧金黄三角
    rect(45, 18, 49, 22, BEAK)
    rect(49, 19, 51, 22, BEAK)

    # 眼睛：单个深色方块
    rect(41, 16, 44, 19, EYE)

    # 翅膀：一道斜向浅影，暗示翅膀收在体侧
    rect(24, 28, 31, 31, CHICK_SHADE)
    rect(22, 30, 27, 33, CHICK_SHADE)

    # 腿：sprite 里几乎看不到腿，只露脚趾
    rect(27, 38, 31, 41, FOOT)
    rect(35, 38, 39, 41, FOOT)

    _outline(img)
    return img


def _outline(img: Image.Image) -> None:
    """给非透明区域描一圈深棕边；鸡与文件夹相接处自然连成一体。"""
    w, h = img.size
    src = img.load()
    edge = []
    for y in range(h):
        for x in range(w):
            if src[x, y][3] == 0:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if nx < 0 or ny < 0 or nx >= w or ny >= h or src[nx, ny][3] == 0:
                    edge.append((x, y))
                    break
    for x, y in edge:
        src[x, y] = OUTLINE


def main() -> None:
    base = build()

    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [base.resize((s, s), Image.NEAREST) for s in sizes]

    frames[-1].save("D:/Agent/_design/icon_preview_256.png")
    frames[-1].save("D:/Agent/_design/smms_icon_256.png")
    frames[-1].save(
        "D:/Agent/_design/smms.ico",
        format="ICO",
        sizes=[(s, s) for s in sizes],
    )

    scale = 6
    canvas = Image.new("RGB", (16 * scale + 32 * scale + 48 * scale + 80, 48 * scale), (246, 246, 241))
    x = 10
    for size in (16, 32, 48):
        big = base.resize((size, size), Image.NEAREST).resize((size * scale, size * scale), Image.NEAREST)
        canvas.paste(big, (x, (48 * scale - size * scale) // 2), big)
        x += size * scale + 20
    canvas.save("D:/Agent/_design/icon_small_preview.png")

    print("已生成 smms.ico：", ", ".join(str(s) for s in sizes))


if __name__ == "__main__":
    main()
