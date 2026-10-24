# 把布局预览导出的顶点/索引/图集还原成 PNG —— 用眼睛看排得对不对。
import os, struct, sys
from PIL import Image

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bin", "Debug", "net8.0", "out")
W, H = [int(v) for v in open(os.path.join(OUT, "atlas.txt")).read().split()]
atlas = Image.frombytes("RGBA", (W, H), open(os.path.join(OUT, "atlas.raw"), "rb").read())
ap = atlas.load()

vtx = open(os.path.join(OUT, "vtx.raw"), "rb").read()
idx = open(os.path.join(OUT, "idx.raw"), "rb").read()
n = len(vtx) // 20
V = [struct.unpack_from("<ffffI", vtx, i * 20) for i in range(n)]
I = list(struct.unpack("<%dH" % (len(idx) // 2), idx))

FW = int(max(v[0] for v in V)) + 2
FH = int(max(v[1] for v in V)) + 2
S = 2                                   # 放大两倍再看
img = Image.new("RGBA", (FW, FH), (10, 12, 15, 255))
px = img.load()
print("画面 %dx%d  顶点 %d  三角形 %d" % (FW, FH, n, len(I) // 3))

def sample(u, v):
    x = min(max(int(u * W), 0), W - 1)
    y = min(max(int(v * H), 0), H - 1)
    return ap[x, y]

for t in range(0, len(I) - 2, 3):
    a, b, c = V[I[t]], V[I[t + 1]], V[I[t + 2]]
    minx = max(0, int(min(a[0], b[0], c[0])) - 1); maxx = min(FW - 1, int(max(a[0], b[0], c[0])) + 1)
    miny = max(0, int(min(a[1], b[1], c[1])) - 1); maxy = min(FH - 1, int(max(a[1], b[1], c[1])) + 1)
    den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
    if abs(den) < 1e-9: continue
    for y in range(miny, maxy + 1):
        for x in range(minx, maxx + 1):
            cx, cy = x + 0.5, y + 0.5
            w0 = ((b[1] - c[1]) * (cx - c[0]) + (c[0] - b[0]) * (cy - c[1])) / den
            w1 = ((c[1] - a[1]) * (cx - c[0]) + (a[0] - c[0]) * (cy - c[1])) / den
            w2 = 1.0 - w0 - w1
            if w0 < -0.002 or w1 < -0.002 or w2 < -0.002: continue
            u = w0 * a[2] + w1 * b[2] + w2 * c[2]
            v = w0 * a[3] + w1 * b[3] + w2 * c[3]
            r = w0 * (a[4] & 255) + w1 * (b[4] & 255) + w2 * (c[4] & 255)
            g = w0 * ((a[4] >> 8) & 255) + w1 * ((b[4] >> 8) & 255) + w2 * ((c[4] >> 8) & 255)
            bl = w0 * ((a[4] >> 16) & 255) + w1 * ((b[4] >> 16) & 255) + w2 * ((c[4] >> 16) & 255)
            al = w0 * ((a[4] >> 24) & 255) + w1 * ((b[4] >> 24) & 255) + w2 * ((c[4] >> 24) & 255)
            sr, sg, sb, sa = sample(u, v)
            src = (al / 255.0) * (sa / 255.0)
            if src <= 0.004: continue
            dst = px[x, y]
            px[x, y] = (int(sr * (r / 255.0) * src + dst[0] * (1 - src)),
                        int(sg * (g / 255.0) * src + dst[1] * (1 - src)),
                        int(sb * (bl / 255.0) * src + dst[2] * (1 - src)), 255)

img.resize((FW * S, FH * S), Image.NEAREST).convert("RGB").save(os.path.join(OUT, (sys.argv[1] if len(sys.argv) > 1 else "preview") + ".png"))
print("已写出 " + os.path.join(OUT, (sys.argv[1] if len(sys.argv) > 1 else "preview") + ".png"))
