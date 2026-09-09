"""像素素材后处理：snap alpha → trim → 众数降采样 → 切图 → 输出到 Assets/ui/pixel/"""
from PIL import Image
import os

RAW = os.path.dirname(os.path.abspath(__file__))
OUT = r"D:\sbox\circleroyale\Assets\ui\pixel"
os.makedirs(OUT, exist_ok=True)


def load(p):
    return Image.open(os.path.join(RAW, p)).convert("RGBA")


def snap_alpha(im):
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            px[x, y] = (r, g, b, 255) if a >= 128 else (0, 0, 0, 0)
    return im


def trim(im):
    bbox = im.getbbox()
    return im.crop(bbox) if bbox else im


def mode_down(im, tw, th):
    """块内众数下采样（忽略透明像素）——像素画缩放不糊边不失格"""
    sw, sh = im.size
    src = im.load()
    out = Image.new("RGBA", (tw, th), (0, 0, 0, 0))
    po = out.load()
    for ty in range(th):
        y0 = int(ty * sh / th)
        y1 = max(y0 + 1, int((ty + 1) * sh / th))
        for tx in range(tw):
            x0 = int(tx * sw / tw)
            x1 = max(x0 + 1, int((tx + 1) * sw / tw))
            cnt = {}
            for sy in range(y0, min(y1, sh)):
                for sx in range(x0, min(x1, sw)):
                    p = src[sx, sy]
                    if p[3] == 0:
                        continue
                    k = (p[0] // 8 * 8, p[1] // 8 * 8, p[2] // 8 * 8)
                    cnt[k] = cnt.get(k, 0) + 1
            if not cnt:
                continue
            best = max(cnt, key=cnt.get)
            po[tx, ty] = (best[0], best[1], best[2], 255)
    return out


def fit_into(im, cw, ch, margin=0.0):
    """内容等比缩放进 cw×ch（留边比例），居中"""
    im = trim(im)
    w, h = im.size
    inner_w, inner_h = int(cw * (1 - margin)), int(ch * (1 - margin))
    s = min(inner_w / w, inner_h / h)
    tw, th = max(1, round(w * s)), max(1, round(h * s))
    small = mode_down(im, tw, th) if (tw != w or th != h) else im
    canvas = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
    canvas.paste(small, ((cw - tw) // 2, (ch - th) // 2))
    return canvas


def components(im, alpha_min=128, min_px=1):
    """alpha 连通域，返回 [(bbox, 像素数)]"""
    w, h = im.size
    px = im.load()
    seen = bytearray(w * h)
    comps = []
    for sy in range(h):
        for sx in range(w):
            if seen[sy * w + sx]:
                continue
            if px[sx, sy][3] < alpha_min:
                seen[sy * w + sx] = 1
                continue
            stack = [(sx, sy)]
            seen[sy * w + sx] = 1
            minx = maxx = sx
            miny = maxy = sy
            n = 0
            while stack:
                x, y = stack.pop()
                n += 1
                minx = min(minx, x); maxx = max(maxx, x)
                miny = min(miny, y); maxy = max(maxy, y)
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < w and 0 <= ny < h and not seen[ny * w + nx] and px[nx, ny][3] >= alpha_min:
                        seen[ny * w + nx] = 1
                        stack.append((nx, ny))
            if n >= min_px:
                comps.append(((minx, miny, maxx + 1, maxy + 1), n))
    return comps


# ---- 1. 球体白模 128×128 ----
ball = snap_alpha(trim(load("ball_raw.png")))
ball128 = fit_into(ball, 128, 128, margin=0.06)
ball128.save(os.path.join(OUT, "ball_body.png"))
print("ball_body:", ball.size, "-> 128x128")

# ---- 2. 绿刺 32×32 ----
spike = snap_alpha(trim(load("spike_raw.png")))
spike32 = fit_into(spike, 32, 32)
spike32.save(os.path.join(OUT, "spike.png"))
print("spike:", spike.size, "-> 32x32")

# ---- 3. 食物：连通域→按 x 排序→每颗 12×12→条带 72×12 ----
food = snap_alpha(load("food_raw.png"))
comps = sorted([c for c in components(food, min_px=200)], key=lambda c: c[0][0])
print("food components:", len(comps), [c[1] for c in comps])
strip = Image.new("RGBA", (72, 12), (0, 0, 0, 0))
for i, (bbox, _) in enumerate(comps[:6]):
    strip.paste(fit_into(food.crop(bbox), 12, 12), (i * 12, 0))
strip.save(os.path.join(OUT, "food.png"))
print("food -> 72x12")

# ---- 4. 表情：连通域→按最大间隙聚成 3 列×2 行→每脸 32×32→图集 96×64 ----
faces_im = snap_alpha(load("faces_raw.png"))
comps = [c for c in components(faces_im, min_px=40)]
print("face components:", len(comps))
cents = [((b[0] + b[2]) / 2, (b[1] + b[3]) / 2) for b, _ in comps]
xs = sorted(c[0] for c in cents)
ys = sorted(c[1] for c in cents)


def split_cuts(vals, k):
    gaps = sorted(range(len(vals) - 1), key=lambda i: vals[i + 1] - vals[i], reverse=True)[:k - 1]
    return sorted((vals[i] + vals[i + 1]) / 2 for i in gaps)


xcuts = split_cuts(xs, 3)
ycuts = split_cuts(ys, 2)
groups = {}
for (bbox, _), (cx, cy) in zip(comps, cents):
    gx = 0 if cx < xcuts[0] else (1 if cx < xcuts[1] else 2)
    gy = 0 if cy < ycuts[0] else 1
    groups.setdefault((gy, gx), []).append(bbox)
print("face groups:", sorted(groups))
atlas = Image.new("RGBA", (96, 64), (0, 0, 0, 0))
for (gy, gx), bboxes in sorted(groups.items()):
    minx = min(b[0] for b in bboxes); miny = min(b[1] for b in bboxes)
    maxx = max(b[2] for b in bboxes); maxy = max(b[3] for b in bboxes)
    face = fit_into(faces_im.crop((minx, miny, maxx, maxy)), 32, 32)
    atlas.paste(face, (gx * 32, gy * 32))
atlas.save(os.path.join(OUT, "faces.png"))
print("faces -> 96x64 (3x2 of 32)")

# ---- 5. 背景网格：测量周期，裁一个整周期→缩到 64×64 ----
bg = load("bg_raw.png")
w, h = bg.size
px = bg.load()
# 基准色 = 全图众数（别取固定像素，可能正好压在网格线上）
cnt = {}
for y in range(0, h, 7):
    for x in range(0, w, 7):
        k = px[x, y][:3]
        cnt[k] = cnt.get(k, 0) + 1
base = max(cnt, key=cnt.get)


def line_centers(fixed, horizontal=False, span=None):
    """沿一条扫描线找网格线中心（连续命中聚成一个中心）"""
    n = span if span else w
    hits = []
    for i in range(n):
        p = px[i, fixed] if horizontal else px[fixed, i]
        if abs(p[0] - base[0]) + abs(p[1] - base[1]) + abs(p[2] - base[2]) > 8:
            hits.append(i)
    centers = []
    for i in hits:
        if centers and i - centers[-1][-1] <= 2:
            centers[-1].append(i)
        else:
            centers.append([i])
    return [sum(c) / len(c) for c in centers]


cx = line_centers(h // 2, horizontal=True)
cy = line_centers(w // 2, horizontal=False, span=h)
print(f"bg line centers x[:5]={[round(v,1) for v in cx[:5]]} count={len(cx)}")
if len(cx) >= 3 and len(cy) >= 3:
    px_period = sum(b - a for a, b in zip(cx, cx[1:])) / (len(cx) - 1)
    py_period = sum(b - a for a, b in zip(cy, cy[1:])) / (len(cy) - 1)
    print(f"period x≈{px_period:.2f} y≈{py_period:.2f}")
    if abs(px_period - py_period) < 2:
        period = (px_period + py_period) / 2
        x0, y0 = cx[0], cy[0]
        kx = int((w - 4 - x0) / period)
        ky = int((h - 4 - y0) / period)
        k = min(kx, ky)
        span = round(period * k)
        tile = bg.crop((round(x0), round(y0), round(x0) + span, round(y0) + span))
        # 目标：64×64 里含 k 个周期 → 每格 64/k，必须整除否则平铺错位
        cell = 64 / k
        if abs(cell - round(cell)) < 0.2 and k <= 16:
            tile = mode_down(tile, 64, 64)
            tile.save(os.path.join(OUT, "bg_tile.png"))
            print(f"bg_tile: {span}px ({k} periods, cell={period:.1f}px) -> 64x64")
        else:
            print(f"bg: 周期 {period:.2f}px 整除 64 失败（k={k}）→ 放弃纹理，走程序化网格")
    else:
        print("bg: x/y 周期不一致 → 放弃纹理，走程序化网格")
else:
    print("bg: 网格线检测失败 → 放弃纹理，走程序化网格")

print("DONE ->", sorted(os.listdir(OUT)))