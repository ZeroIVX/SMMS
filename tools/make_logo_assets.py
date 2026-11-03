"""把 folder-oncream 的米色底去掉，生成透明 PNG 与多尺寸 ICO。

背景与蛋壳内壁都是浅色，不能简单按颜色阈值删除；
从四条边做连通填充，只清除与画布边缘连通的背景区域。
"""

from collections import deque

from PIL import Image

SRC = "D:/Agent/_design/logo/final/smms-icon-folder-oncream.png"
OUT_PNG = "D:/Agent/_design/logo/final/smms-icon-folder-transparent.png"
OUT_ICO = "D:/Agent/_design/logo/final/smms-icon-folder.ico"

# 背景与前景的判定容差：欧氏距离
HARD = 26.0    # 小于它视为纯背景
SOFT = 62.0    # 介于两者之间按比例给半透明，保留边缘抗锯齿


def dist2(a, b):
    return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2


def main() -> None:
    im = Image.open(SRC).convert("RGB")
    w, h = im.size
    px = im.load()

    # 用四角的平均色作为背景基准
    corners = [px[0, 0], px[w - 1, 0], px[0, h - 1], px[w - 1, h - 1]]
    bg = tuple(sum(c[i] for c in corners) // len(corners) for i in range(3))
    print("背景色:", bg)

    # ---- 从边缘泛洪，找出与画布相连的背景 ----
    background = bytearray(w * h)
    queue = deque()

    def push(x, y):
        if 0 <= x < w and 0 <= y < h and not background[y * w + x]:
            if dist2(px[x, y], bg) <= HARD * HARD:
                background[y * w + x] = 1
                queue.append((x, y))

    for x in range(w):
        push(x, 0)
        push(x, h - 1)
    for y in range(h):
        push(0, y)
        push(w - 1, y)

    while queue:
        x, y = queue.popleft()
        push(x + 1, y)
        push(x - 1, y)
        push(x, y + 1)
        push(x, y - 1)

    # ---- 生成 RGBA ----
    out = Image.new("RGBA", (w, h))
    op = out.load()

    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]

            if background[y * w + x]:
                op[x, y] = (r, g, b, 0)
                continue

            # 紧贴背景的前景像素做半透明过渡，消掉锯齿
            near_bg = False
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and background[ny * w + nx]:
                    near_bg = True
                    break

            if near_bg:
                d = dist2((r, g, b), bg) ** 0.5
                if d <= HARD:
                    op[x, y] = (r, g, b, 0)
                elif d < SOFT:
                    alpha = int(255 * (d - HARD) / (SOFT - HARD))
                    op[x, y] = (r, g, b, alpha)
                else:
                    op[x, y] = (r, g, b, 255)
            else:
                op[x, y] = (r, g, b, 255)

    out.save(OUT_PNG)

    # ---- 多尺寸 ICO ----
    sizes = [16, 24, 32, 48, 64, 128, 256]
    big = out.resize((256, 256), Image.LANCZOS)
    big.save(OUT_ICO, format="ICO", sizes=[(s, s) for s in sizes])

    # 预览：透明区域铺浅灰格，便于检查残留白边
    preview = Image.new("RGB", (w, h), (250, 250, 248))
    preview.paste(out, (0, 0), out)
    preview.save("D:/Agent/_design/logo/final/_check_transparent.png")

    leftover = sum(1 for y in range(h) for x in range(w)
                   if out.getpixel((x, y))[3] > 0
                   and abs(out.getpixel((x, y))[0] - bg[0]) < 6
                   and abs(out.getpixel((x, y))[1] - bg[1]) < 6
                   and abs(out.getpixel((x, y))[2] - bg[2]) < 6)
    print(f"已生成透明 PNG 与 ICO；残留近似背景色像素: {leftover}")


if __name__ == "__main__":
    main()
