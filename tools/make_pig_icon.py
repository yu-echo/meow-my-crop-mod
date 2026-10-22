"""画一个可爱猪头图标（128x128 RGBA），4 倍超采样后缩小，边缘干净。"""
from PIL import Image, ImageDraw

S = 4
W = 128 * S
img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(img)


def P(x, y):
    """按 128 画布写坐标，自动放大。"""
    return (x * S, y * S)


HEAD = (255, 190, 205, 255)
HEAD_LINE = (228, 133, 158, 255)
EAR_IN = (255, 150, 176, 255)
SNOUT = (255, 158, 184, 255)
SNOUT_LINE = (214, 112, 139, 255)
NOSE = (196, 92, 118, 255)
EYE = (74, 44, 30, 255)
BLUSH = (255, 130, 158, 110)

# ---- 耳朵（先画，压在脸下面）：圆角三角，猪耳朵的形状 ----
def ear(pts, inner):
    d.polygon([P(*p) for p in pts], fill=HEAD, outline=HEAD_LINE, width=3 * S)
    d.polygon([P(*p) for p in inner], fill=EAR_IN)

ear([(24, 56), (30, 14), (68, 42)], [(35, 50), (38, 26), (58, 41)])
ear([(104, 56), (98, 14), (60, 42)], [(93, 50), (90, 26), (70, 41)])

# ---- 脸 ----
d.ellipse([P(10, 32), P(118, 124)], fill=HEAD, outline=HEAD_LINE, width=4 * S)

# ---- 腮红 ----
d.ellipse([P(15, 80), P(38, 95)], fill=BLUSH)
d.ellipse([P(90, 80), P(113, 95)], fill=BLUSH)

# ---- 眼睛：圆眼 + 高光，缩到 40 像素也还是可爱的脸 ----
d.ellipse([P(30, 58), P(52, 80)], fill=EYE)
d.ellipse([P(76, 58), P(98, 80)], fill=EYE)
d.ellipse([P(34, 62), P(42, 70)], fill=(255, 255, 255, 235))
d.ellipse([P(80, 62), P(88, 70)], fill=(255, 255, 255, 235))

# ---- 鼻子 ----
d.ellipse([P(41, 78), P(87, 106)], fill=SNOUT, outline=SNOUT_LINE, width=3 * S)
d.ellipse([P(49, 86), P(58, 98)], fill=NOSE)
d.ellipse([P(70, 86), P(79, 98)], fill=NOSE)



# 128 给游戏里的标签图标，256 给 exe 里的 logo，512 备用
big = img.resize((256, 256), Image.LANCZOS)
big.save("pig256.png")
big.resize((128, 128), Image.LANCZOS).save("pig_icon.png")

# 多尺寸 ico：任务栏/标题栏/资源管理器都用得上，避免小尺寸被硬缩得糊
sizes = [16, 24, 32, 48, 64, 128, 256]
big.save("pig.ico", sizes=[(s, s) for s in sizes])
print("saved pig_icon.png(128) pig256.png pig.ico")
