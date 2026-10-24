# -*- coding: utf-8 -*-
"""把一组 SVG 图标打成一份 TTF 图标字体。

用法：把 .svg 丢进 tools/icons/svg/（**文件名就是图标名**），然后

    python tools/icons/svg2iconfont.py

产出四样东西：
    dist/ADOFAIEditorStudio/UiIcons.ttf   字体本体
    UiIconTable.cs                        码点表（名字 → 码点常量）
    tools/icons/icons.json                同一份映射，给人看的
    tools/icons/preview.png               预览图 —— **形状只能靠看，别跳过**

自检（不读 svg 目录，跑内置样例，覆盖描边/填充/evenodd/圆弧/变换/各种接头）：

    python tools/icons/svg2iconfont.py --selftest

为什么要自己转：ImGui 画字走 stb_truetype，只认 TTF/OTF，不认 SVG，也不认 PNG
（PNG 每张是一张独立纹理，在 Unity 桥里各占一条 Mesh）。所以 SVG 只能是原料，
得先离线变成字体。

描边怎么办：Lucide / Feather / Tabler 这一族的图标是**描边**的（`fill="none"`
配 `stroke`），而字体里只存填充轮廓，没有"线宽"这个概念。转描边有两条路 ——
装 Inkscape 走 `stroke → path`，或者自己做偏移展开。这里走后者，而且不引入任何
几何库：

  把每条线段展开成一个四边形、每个拐角和端点展开成一个圆，**全部按同一个绕向**
  输出。TrueType 是 nonzero 缠绕填充，同向的块重叠处只算一次并集，而闭合环内部
  没有任何块覆盖、缠绕数为 0，自然就成了洞。于是不需要求交/求并，形状就是对的。

依赖：fontTools（跟 make-font-subset.py 同一个）、Pillow（只用来出预览图）。
"""

import argparse
import json
import math
import os
import re
import sys
import xml.etree.ElementTree as ET

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ICON_DIR = os.path.join(ROOT, "tools", "icons")
SVG_DIR = os.path.join(ICON_DIR, "svg")
OUT_FONT = os.path.join(ROOT, "dist", "ADOFAIEditorStudio", "UiIcons.ttf")
OUT_CS = os.path.join(ROOT, "UiIconTable.cs")
OUT_JSON = os.path.join(ICON_DIR, "icons.json")
OUT_PREVIEW = os.path.join(ICON_DIR, "preview.png")

# 码点落在私有区。**必须在 E000 往上**，免得撞上 UiGlyphTable 里那张汉字表
# （一级二级合计 6500 字，横跨 4E00~9FFF 和 3400~4DBF）。
CODEPOINT_BASE = 0xE000

# ------------------------------------------------------------------ 字身尺寸
#
# 图标画在一个 1000 单位的 em 里，正方形画布 800 单位、水平居中、垂直中心落在
# 350（= 行盒中点）。行盒 ascender 850 / descender -150，合起来正好 1em —— 这样
# 图标字体和正文字体按同一个字号混排时行高一致。
UPEM = 1000
ASCENDER = 850
DESCENDER = -150
ICON_BOX = 800.0
ICON_CX = 500.0
ICON_CY = 350.0

# 曲线打散的精度，单位是**字体单位**。1000 单位的 em 里 2 单位 = 0.2%，
# 图标在屏幕上最大也就 20px 上下，折线误差远在半个像素以下。
FLATTEN_TOL = 2.0

# 段号。SVG 路径里没有 Z 的结尾，字体里每个子路径都得显式闭合。
_NUM_RE = re.compile(r"[+-]?(?:\d*\.\d+|\d+\.?)(?:[eE][+-]?\d+)?")
_CMD_RE = re.compile(r"[MmLlHhVvCcSsQqTtAaZz]")


# ==================================================================== 小工具

def _num(text, pos):
    """从 pos 起读一个数，返回 (值, 新位置)。读不到就返回 (None, pos)。"""
    m = _NUM_RE.match(text, pos)
    if not m or m.end() == pos:
        return None, pos
    return float(m.group()), m.end()


def signed_area(pts):
    """多边形的有向面积。用来判绕向，也用来把退化轮廓挑出去。"""
    total = 0.0
    n = len(pts)
    for i in range(n):
        x0, y0 = pts[i]
        x1, y1 = pts[(i + 1) % n]
        total += x0 * y1 - x1 * y0
    return total * 0.5


def point_in_poly(x, y, pts):
    """射线法。用在 evenodd 转 nonzero 时判嵌套。"""
    inside = False
    n = len(pts)
    j = n - 1
    for i in range(n):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y):
            t = (y - yi) / (yj - yi)
            if x < xi + t * (xj - xi):
                inside = not inside
        j = i
    return inside


def dedupe(pts, eps=1e-6):
    """去掉挨得太近的点。零长度的边会让字体的轮廓解析出问题。"""
    out = []
    for p in pts:
        if not out or abs(p[0] - out[-1][0]) > eps or abs(p[1] - out[-1][1]) > eps:
            out.append(p)
    if len(out) > 1 and abs(out[0][0] - out[-1][0]) <= eps and abs(out[0][1] - out[-1][1]) <= eps:
        out.pop()
    return out


# ==================================================================== 变换矩阵

class Matrix(object):
    """SVG 的 2D 仿射变换，(a, b, c, d, e, f) 对应

        | a  c  e |
        | b  d  f |
        | 0  0  1 |
    """

    __slots__ = ("a", "b", "c", "d", "e", "f")

    def __init__(self, a=1.0, b=0.0, c=0.0, d=1.0, e=0.0, f=0.0):
        self.a, self.b, self.c, self.d, self.e, self.f = a, b, c, d, e, f

    def mul(self, o):
        """self ∘ other —— 先作用 other，再作用 self。"""
        return Matrix(
            self.a * o.a + self.c * o.b,
            self.b * o.a + self.d * o.b,
            self.a * o.c + self.c * o.d,
            self.b * o.c + self.d * o.d,
            self.a * o.e + self.c * o.f + self.e,
            self.b * o.e + self.d * o.f + self.f,
        )

    def apply(self, p):
        x, y = p
        return (self.a * x + self.c * y + self.e, self.b * x + self.d * y + self.f)

    def max_scale(self):
        """最大奇异值，约等于"这个变换最多把长度放大几倍"。
        线宽和打散精度都得按它折算。"""
        # 2x2 矩阵 [[a,c],[b,d]] 的奇异值：解 det(Mᵀ M − λI) = 0 取大根
        a, b, c, d = self.a, self.b, self.c, self.d
        tr = a * a + b * b + c * c + d * d
        det = (a * d - b * c) ** 2
        disc = max(0.0, tr * tr / 4.0 - det)
        return math.sqrt(max(1e-12, tr / 2.0 + math.sqrt(disc)))

    def is_conformal(self, tol=1e-3):
        """是不是等比的（旋转+缩放+平移）。描边展开只在等比下才是精确的。"""
        # 两个基向量等长且正交
        l1 = math.hypot(self.a, self.b)
        l2 = math.hypot(self.c, self.d)
        if l1 < 1e-12 or l2 < 1e-12:
            return True
        if abs(l1 - l2) / max(l1, l2) > tol:
            return False
        dot = (self.a * self.c + self.b * self.d) / (l1 * l2)
        return abs(dot) <= tol


def parse_transform(text):
    if not text:
        return Matrix()
    out = Matrix()
    for name, argstr in re.findall(r"([a-zA-Z]+)\s*\(([^)]*)\)", text):
        args = [float(v) for v in _NUM_RE.findall(argstr)]
        name = name.strip()
        if name == "matrix" and len(args) == 6:
            m = Matrix(*args)
        elif name == "translate":
            m = Matrix(e=args[0] if args else 0.0,
                       f=args[1] if len(args) > 1 else 0.0)
        elif name == "scale":
            sx = args[0] if args else 1.0
            sy = args[1] if len(args) > 1 else sx
            m = Matrix(a=sx, d=sy)
        elif name == "rotate":
            ang = math.radians(args[0] if args else 0.0)
            cs, sn = math.cos(ang), math.sin(ang)
            m = Matrix(a=cs, b=sn, c=-sn, d=cs)
            if len(args) >= 3:
                cx, cy = args[1], args[2]
                m = Matrix(e=cx, f=cy).mul(m).mul(Matrix(e=-cx, f=-cy))
        elif name == "skewX":
            m = Matrix(c=math.tan(math.radians(args[0] if args else 0.0)))
        elif name == "skewY":
            m = Matrix(b=math.tan(math.radians(args[0] if args else 0.0)))
        else:
            continue
        out = out.mul(m)
    return out


# ==================================================================== 路径解析

def parse_path(d):
    """把 d 拆成 [(cmd, [数字...]), ...]。只做词法，不做几何。"""
    tokens = []
    pos = 0
    while pos < len(d):
        ch = d[pos]
        if ch in " \t\r\n,":
            pos += 1
            continue
        if _CMD_RE.match(ch):
            tokens.append((ch, []))
            pos += 1
            continue
        if not tokens:
            # 路径开头就是个数字 —— SVG 允许省略 M，按 M 处理
            tokens.append(("M", []))
        val, npos = _num(d, pos)
        if val is None:
            pos += 1
            continue
        tokens[-1][1].append(val)
        pos = npos
    return tokens


def flatten_cubic(p0, p1, p2, p3, tol, out):
    """自适应细分三次贝塞尔。tol 是允许的弦高误差。"""
    # 用控制点到弦的距离做平坦判据，够用且快
    dx = p3[0] - p0[0]
    dy = p3[1] - p0[1]
    d1 = abs((p1[0] - p3[0]) * dy - (p1[1] - p3[1]) * dx)
    d2 = abs((p2[0] - p3[0]) * dy - (p2[1] - p3[1]) * dx)
    if (d1 + d2) ** 2 <= tol * tol * (dx * dx + dy * dy):
        out.append(p3)
        return
    # 二分：de Casteljau
    def mid(a, b):
        return ((a[0] + b[0]) * 0.5, (a[1] + b[1]) * 0.5)

    p01 = mid(p0, p1)
    p12 = mid(p1, p2)
    p23 = mid(p2, p3)
    p012 = mid(p01, p12)
    p123 = mid(p12, p23)
    mid_all = mid(p012, p123)
    flatten_cubic(p0, p01, p012, mid_all, tol, out)
    flatten_cubic(mid_all, p123, p23, p3, tol, out)


def flatten_quad(p0, p1, p2, tol, out):
    # 二次升三次
    c1 = (p0[0] + 2.0 / 3.0 * (p1[0] - p0[0]), p0[1] + 2.0 / 3.0 * (p1[1] - p0[1]))
    c2 = (p2[0] + 2.0 / 3.0 * (p1[0] - p2[0]), p2[1] + 2.0 / 3.0 * (p1[1] - p2[1]))
    flatten_cubic(p0, c1, c2, p2, tol, out)


def flatten_arc(p0, rx, ry, phi_deg, large, sweep, p1, tol, out):
    """SVG 弧 → 折线。按规范 F.6.5 先做端点参数化，再按角度采样。"""
    x0, y0 = p0
    x1, y1 = p1
    if abs(x0 - x1) < 1e-12 and abs(y0 - y1) < 1e-12:
        return
    rx, ry = abs(rx), abs(ry)
    if rx < 1e-12 or ry < 1e-12:
        out.append(p1)
        return

    phi = math.radians(phi_deg % 360.0)
    cs, sn = math.cos(phi), math.sin(phi)
    # 转到弧自己的坐标系
    dx2, dy2 = (x0 - x1) * 0.5, (y0 - y1) * 0.5
    xp = cs * dx2 + sn * dy2
    yp = -sn * dx2 + cs * dy2

    # 半径不够大就放大到刚好能连上
    lam = (xp * xp) / (rx * rx) + (yp * yp) / (ry * ry)
    if lam > 1.0:
        s = math.sqrt(lam)
        rx *= s
        ry *= s

    num = rx * rx * ry * ry - rx * rx * yp * yp - ry * ry * xp * xp
    den = rx * rx * yp * yp + ry * ry * xp * xp
    coef = math.sqrt(max(0.0, num / den)) if den > 1e-12 else 0.0
    if large == sweep:
        coef = -coef
    cxp = coef * rx * yp / ry
    cyp = -coef * ry * xp / rx
    cx = cs * cxp - sn * cyp + (x0 + x1) * 0.5
    cy = sn * cxp + cs * cyp + (y0 + y1) * 0.5

    def angle(ux, uy, vx, vy):
        dot = ux * vx + uy * vy
        n = math.hypot(ux, uy) * math.hypot(vx, vy)
        if n < 1e-12:
            return 0.0
        a = math.acos(max(-1.0, min(1.0, dot / n)))
        return -a if (ux * vy - uy * vx) < 0 else a

    ux, uy = (xp - cxp) / rx, (yp - cyp) / ry
    vx, vy = (-xp - cxp) / rx, (-yp - cyp) / ry
    theta0 = angle(1.0, 0.0, ux, uy)
    dtheta = angle(ux, uy, vx, vy)
    if sweep == 0 and dtheta > 0:
        dtheta -= 2.0 * math.pi
    elif sweep == 1 and dtheta < 0:
        dtheta += 2.0 * math.pi

    # 步数按弧长和误差反推
    scale = max(rx, ry)
    step = 2.0 * math.acos(max(0.0, min(1.0, 1.0 - tol / max(scale, 1e-9))))
    if step <= 0:
        step = math.pi / 12.0
    count = max(2, int(math.ceil(abs(dtheta) / step)))

    for i in range(1, count + 1):
        t = theta0 + dtheta * i / count
        ex, ey = rx * math.cos(t), ry * math.sin(t)
        out.append((cs * ex - sn * ey + cx, sn * ex + cs * ey + cy))


def flatten_path(d, tol):
    """把路径文本打散成折线。返回 (子路径列表, 是相对还是绝对……)。
    每个子路径是 dict(points=[...], closed=bool)。"""
    subs = []
    cur = None
    x = y = 0.0          # 当前点
    sx = sy = 0.0        # 子路径起点
    prev_cmd = ""
    # 上一段三次/二次曲线的第二控制点，S/T 要用
    last_c2 = None
    last_q1 = None

    def start(px, py):
        nonlocal cur
        cur = {"points": [(px, py)], "closed": False}
        subs.append(cur)

    def line_to(px, py):
        nonlocal cur
        if cur is None:
            start(x, y)
        cur["points"].append((px, py))

    tokens = parse_path(d)
    for cmd, args in tokens:
        rel = cmd.islower()
        up = cmd.upper()

        if up == "M":
            i = 0
            first = True
            while i + 1 < len(args):
                px, py = args[i], args[i + 1]
                if rel:
                    px += x
                    py += y
                if first:
                    start(px, py)
                    first = False
                else:
                    line_to(px, py)
                x, y = px, py
                i += 2
            sx, sy = x, y
            last_c2 = last_q1 = None

        elif up == "L":
            for i in range(0, len(args) - 1, 2):
                px, py = args[i], args[i + 1]
                if rel:
                    px += x
                    py += y
                line_to(px, py)
                x, y = px, py
            last_c2 = last_q1 = None

        elif up == "H":
            for v in args:
                px = x + v if rel else v
                line_to(px, y)
                x = px
            last_c2 = last_q1 = None

        elif up == "V":
            for v in args:
                py = y + v if rel else v
                line_to(x, py)
                y = py
            last_c2 = last_q1 = None

        elif up in ("C", "S"):
            step = 6 if up == "C" else 4
            for i in range(0, len(args) - step + 1, step):
                if up == "C":
                    c1 = (args[i], args[i + 1])
                    c2 = (args[i + 2], args[i + 3])
                    endp = (args[i + 4], args[i + 5])
                    if rel:
                        c1 = (c1[0] + x, c1[1] + y)
                        c2 = (c2[0] + x, c2[1] + y)
                        endp = (endp[0] + x, endp[1] + y)
                else:
                    # **平滑段的第一控制点是"上一段第二控制点关于当前点的对称点"。**
                    #
                    # 之前直接拿 last_c2 用，是错的 —— 那等于把控制点原地不动，
                    # 曲线在接缝处折出一个尖角。这个尖角在斜接（miter）下会炸成
                    # 放射状的刺，圆接头下被圆盖住，所以很容易漏看。
                    # 另外对称点是在**绝对坐标**下算的，别跟着 rel 再偏移一次。
                    c2 = (args[i], args[i + 1])
                    endp = (args[i + 2], args[i + 3])
                    if rel:
                        c2 = (c2[0] + x, c2[1] + y)
                        endp = (endp[0] + x, endp[1] + y)
                    if last_c2 is not None:
                        c1 = (2.0 * x - last_c2[0], 2.0 * y - last_c2[1])
                    else:
                        c1 = (x, y)
                px, py = endp
                if cur is None:
                    start(x, y)
                pts = []
                flatten_cubic((x, y), c1, c2, (px, py), tol, pts)
                cur["points"].extend(pts)
                last_c2 = c2
                x, y = px, py
            last_q1 = None

        elif up in ("Q", "T"):
            step = 4 if up == "Q" else 2
            for i in range(0, len(args) - step + 1, step):
                if up == "Q":
                    q1 = (args[i], args[i + 1])
                    endp = (args[i + 2], args[i + 3])
                    if rel:
                        q1 = (q1[0] + x, q1[1] + y)
                        endp = (endp[0] + x, endp[1] + y)
                else:
                    # 同上：T 的控制点是上一段控制点关于当前点的对称点
                    endp = (args[i], args[i + 1])
                    if rel:
                        endp = (endp[0] + x, endp[1] + y)
                    if last_q1 is not None:
                        q1 = (2.0 * x - last_q1[0], 2.0 * y - last_q1[1])
                    else:
                        q1 = (x, y)
                px, py = endp
                if cur is None:
                    start(x, y)
                pts = []
                flatten_quad((x, y), q1, (px, py), tol, pts)
                cur["points"].extend(pts)
                last_q1 = q1
                x, y = px, py
            last_c2 = None

        elif up == "A":
            for i in range(0, len(args) - 6, 7):
                rx, ry = args[i], args[i + 1]
                rot = args[i + 2]
                large = int(args[i + 3])
                sweep = int(args[i + 4])
                px, py = args[i + 5], args[i + 6]
                if rel:
                    px += x
                    py += y
                if cur is None:
                    start(x, y)
                pts = []
                flatten_arc((x, y), rx, ry, rot, large, sweep, (px, py), tol, pts)
                cur["points"].extend(pts)
                x, y = px, py
            last_c2 = last_q1 = None

        elif up == "Z":
            if cur is not None:
                cur["closed"] = True
                # 起点和终点重合时去掉重复的那一点
                cur["points"] = dedupe(cur["points"])
            x, y = sx, sy
            cur = None
            last_c2 = last_q1 = None

        prev_cmd = cmd

    # 收尾：去掉重复点，剔掉不足三点的
    result = []
    for s in subs:
        pts = dedupe(s["points"])
        if len(pts) >= 2:
            s["points"] = pts
            result.append(s)
    return result


# ==================================================================== 形状 → 路径

def shape_to_path(el, tag):
    """把 rect / circle / ellipse / line / polyline / polygon 变成 d 字符串。"""
    def g(name, default=0.0):
        v = el.get(name)
        if v is None:
            return default
        v = v.strip().rstrip("%")
        try:
            return float(v)
        except ValueError:
            return default

    if tag == "rect":
        x, y, w, h = g("x"), g("y"), g("width"), g("height")
        if w <= 0 or h <= 0:
            return None
        rx, ry = g("rx", -1.0), g("ry", -1.0)
        if rx < 0:
            rx = ry if ry > 0 else 0.0
        if ry < 0:
            ry = rx
        rx = min(rx, w * 0.5)
        ry = min(ry, h * 0.5)
        if rx <= 0 or ry <= 0:
            return "M%f %f H%f V%f H%f Z" % (x, y, x + w, y + h, x)
        # 圆角矩形：四段直线 + 四个椭圆弧
        return ("M%f %f H%f A%f %f 0 0 1 %f %f V%f A%f %f 0 0 1 %f %f "
                "H%f A%f %f 0 0 1 %f %f V%f A%f %f 0 0 1 %f %f Z") % (
            x + rx, y,
            x + w - rx,
            rx, ry, x + w, y + ry,
            y + h - ry,
            rx, ry, x + w - rx, y + h,
            x + rx,
            rx, ry, x, y + h - ry,
            y + ry,
            rx, ry, x + rx, y)

    if tag in ("circle", "ellipse"):
        cx, cy = g("cx"), g("cy")
        rx = g("r") if tag == "circle" else g("rx")
        ry = g("r") if tag == "circle" else g("ry")
        if rx <= 0 or ry <= 0:
            return None
        return ("M%f %f A%f %f 0 1 0 %f %f A%f %f 0 1 0 %f %f Z") % (
            cx - rx, cy, rx, ry, cx + rx, cy, rx, ry, cx - rx, cy)

    if tag == "line":
        return "M%f %f L%f %f" % (g("x1"), g("y1"), g("x2"), g("y2"))

    if tag in ("polyline", "polygon"):
        raw = el.get("points") or ""
        nums = [float(v) for v in _NUM_RE.findall(raw)]
        if len(nums) < 4:
            return None
        d = "M%f %f" % (nums[0], nums[1])
        for i in range(2, len(nums) - 1, 2):
            d += " L%f %f" % (nums[i], nums[i + 1])
        if tag == "polygon":
            d += " Z"
        return d

    return None


# ==================================================================== 描边展开

def _circle_pts(cx, cy, r, tol):
    if r <= 1e-9:
        return []
    step = 2.0 * math.acos(max(0.0, min(1.0, 1.0 - tol / r)))
    if step <= 1e-6:
        step = math.pi / 8.0
    count = max(6, int(math.ceil(2.0 * math.pi / step)))
    return [(cx + r * math.cos(2.0 * math.pi * i / count),
             cy + r * math.sin(2.0 * math.pi * i / count)) for i in range(count)]


def line_isect(p, u, q, v):
    """两条参数直线 p+t·u 和 q+s·v 的交点。平行返回 None。"""
    den = u[0] * v[1] - u[1] * v[0]
    if abs(den) < 1e-12:
        return None
    t = ((q[0] - p[0]) * v[1] - (q[1] - p[1]) * v[0]) / den
    return (p[0] + u[0] * t, p[1] + u[1] * t)


def expand_stroke(points, closed, width, cap, join, tol, miter_limit=4.0):
    """描边 → 一堆同向的封闭折线。

    **同向是关键。** nonzero 缠绕下，同向重叠只算并集；闭合环内部没被任何块
    覆盖，缠绕数为 0，自动成为洞。所以不需要任何布尔运算。
    """
    out = []
    hw = width * 0.5
    pts = dedupe(points)
    n = len(pts)
    if n < 2:
        if n == 1 and cap == "round":
            c = _circle_pts(pts[0][0], pts[0][1], hw, tol)
            if c:
                out.append(c)
        return out

    # 闭合的话把首尾接上
    segs = []
    for i in range(n - 1):
        segs.append((pts[i], pts[i + 1], True))
    if closed:
        segs.append((pts[-1], pts[0], True))

    for (p0, p1, _interior) in segs:
        dx = p1[0] - p0[0]
        dy = p1[1] - p0[1]
        length = math.hypot(dx, dy)
        if length <= 1e-12:
            continue
        # 单位法线
        nx, ny = -dy / length * hw, dx / length * hw
        out.append([(p0[0] + nx, p0[1] + ny),
                    (p1[0] + nx, p1[1] + ny),
                    (p1[0] - nx, p1[1] - ny),
                    (p0[0] - nx, p0[1] - ny)])

    # 拐角
    joints = list(range(1, n - 1))
    if closed:
        joints = list(range(n))
    for i in joints:
        a = pts[(i - 1) % n]
        b = pts[i]
        c = pts[(i + 1) % n]
        d1x, d1y = b[0] - a[0], b[1] - a[1]
        d2x, d2y = c[0] - b[0], c[1] - b[1]
        l1 = math.hypot(d1x, d1y)
        l2 = math.hypot(d2x, d2y)
        if l1 < 1e-12 or l2 < 1e-12:
            continue
        u1 = (d1x / l1, d1y / l1)
        u2 = (d2x / l2, d2y / l2)
        # 共线的拐角不用管 —— 打散出来的曲线上一大堆这种，白画
        if abs(u1[0] * u2[1] - u1[1] * u2[0]) < 1e-9:
            continue

        if join == "round":
            circle = _circle_pts(b[0], b[1], hw, tol)
            if circle:
                out.append(circle)
            continue

        # **接头必须用边上的偏移点算，不能用中心线的三点。**
        #
        # 用 [a, b, c] 是个大三角，跨度是两整条边长，会把转角整个糊掉 ——
        # 表现出来就是描边外侧一圈毛刺。偏移点算出来的才是真正的斜接三角。
        #
        # 只补**外侧**：内侧那块被两条边的四边形盖着，补上去是多余的，
        # 还会在锐角上多出个尖。外侧是哪边由转向定 —— 叉积为正（朝 +法线
        # 方向转）时外侧在 −法线那一边。
        cross = u1[0] * u2[1] - u1[1] * u2[0]
        sign = -1.0 if cross > 0 else 1.0
        n1 = (sign * -u1[1] * hw, sign * u1[0] * hw)
        n2 = (sign * -u2[1] * hw, sign * u2[0] * hw)
        o1 = (b[0] + n1[0], b[1] + n1[1])
        o2 = (b[0] + n2[0], b[1] + n2[1])
        if join == "miter":
            tip = line_isect(o1, u1, o2, u2)
            # 斜接尖太长就退成斜切 —— 和 SVG 的 miterlimit 一个意思
            if tip is not None and math.hypot(tip[0] - b[0], tip[1] - b[1]) <= miter_limit * hw:
                # **补的是四边形 [o1, tip, o2, b]，不是三角形 [o1, tip, o2]。**
                #
                # 少写一个 b 就只填了缺口的一半 —— 90° 直角那里漏掉的小三角
                # 不显眼，但曲线上一圈拐角连起来就是放射状的梳齿。
                out.append([o1, tip, o2, b])
                continue
        # 斜切：外侧两点直接连一条直线，补出来的区域是 [o1, o2, b]
        out.append([o1, o2, b])

    # 端点
    if not closed:
        if cap == "round":
            for p in (pts[0], pts[-1]):
                circle = _circle_pts(p[0], p[1], hw, tol)
                if circle:
                    out.append(circle)
        elif cap == "square":
            for (p0, p1) in ((pts[0], pts[1]), (pts[-1], pts[-2])):
                dx = p0[0] - p1[0]
                dy = p0[1] - p1[1]
                length = math.hypot(dx, dy)
                if length <= 1e-12:
                    continue
                ux, uy = dx / length, dy / length
                nx, ny = -uy * hw, ux * hw
                ex, ey = ux * hw, uy * hw
                out.append([(p0[0] + nx + ex, p0[1] + ny + ey),
                            (p0[0] + nx, p0[1] + ny),
                            (p0[0] - nx, p0[1] - ny),
                            (p0[0] - nx + ex, p0[1] - ny + ey)])
    return out


# ==================================================================== SVG 读取

def _local(tag):
    return tag.split("}")[-1]


def _style_of(el, inherited):
    """把当前元素上的样式并到继承来的那一份上。只认呈现属性和 style=""。"""
    st = dict(inherited)
    props = {}
    style = el.get("style")
    if style:
        for decl in style.split(";"):
            if ":" in decl:
                k, v = decl.split(":", 1)
                props[k.strip()] = v.strip()
    for key in ("fill", "stroke", "stroke-width", "fill-rule",
                "stroke-linecap", "stroke-linejoin", "stroke-miterlimit",
                "opacity", "stroke-opacity", "fill-opacity"):
        if el.get(key) is not None:
            props[key] = el.get(key).strip()
    st.update(props)
    return st


def _num_or(v, default):
    if v is None:
        return default
    try:
        return float(str(v).replace("px", "").strip())
    except ValueError:
        return default


def collect_shapes(path):
    """遍历 SVG，返回 (viewBox, [(子路径, 样式, 变换), ...])。

    样式在这里已经解析成"最终生效"的那一份（继承 + 元素自己的属性），
    后面不再关心 SVG 的层叠规则。
    """
    tree = ET.parse(path)
    root = tree.getroot()

    viewbox = None
    vb = root.get("viewBox")
    if vb:
        nums = [float(v) for v in _NUM_RE.findall(vb)]
        if len(nums) == 4 and nums[2] > 0 and nums[3] > 0:
            viewbox = nums
    if viewbox is None:
        w = _num_or(root.get("width"), 0.0)
        h = _num_or(root.get("height"), 0.0)
        if w > 0 and h > 0:
            viewbox = [0.0, 0.0, w, h]
        else:
            viewbox = [0.0, 0.0, 24.0, 24.0]   # 默认按 lucide 那套 24 网格

    base_style = {
        "fill": "black",          # SVG 默认就是黑填充
        "stroke": "none",
        "stroke-width": "1",
        "fill-rule": "nonzero",
        "stroke-linecap": "butt",
        "stroke-linejoin": "miter",
    }
    items = []

    def walk(el, ctm, style):
        for child in el:
            tag = _local(child.tag)
            if tag in ("defs", "title", "desc", "metadata", "style", "clipPath",
                       "mask", "filter", "symbol", "use"):
                # use / symbol 之类先不展开 —— 图标集里少见，真碰上了再补
                continue
            child_ctm = ctm.mul(parse_transform(child.get("transform")))
            child_style = _style_of(child, style)

            if tag in ("g", "svg", "a"):
                walk(child, child_ctm, child_style)
                continue

            if tag == "path":
                d = child.get("d")
            else:
                d = shape_to_path(child, tag)
            if not d:
                continue
            items.append((d, child_style, child_ctm))

    # **根元素自己的样式要先算进去。**
    #
    # 图标集习惯把 `fill="none" stroke="black" stroke-width="2"` 写在 <svg> 上，
    # 子元素一个属性都不带、全靠继承。只从 base_style 起步的话这一份就被整个丢掉，
    # 描边图标会被当成实心填充 —— 形状看着"有"，但全错。
    root_style = _style_of(root, base_style)
    walk(root, parse_transform(root.get("transform")), root_style)
    return viewbox, items


# ==================================================================== 组装轮廓

def build_contours(path, tol_user):
    """一个 SVG → 字体单位下的一堆轮廓（每个是一串 (x, y)）。

    流程：解析 → 打散成折线（用户坐标）→ 描边就地展开 → 整个图形乘上
    viewBox→字身的映射，落到字体坐标。**描边在用户坐标里展开，所以线宽不受
    viewBox 尺度影响**；映射是等比缩放，展开的精度照样成立。
    """
    viewbox, items = collect_shapes(path)
    vx, vy, vw, vh = viewbox
    scale = ICON_BOX / max(vw, vh)

    def to_font(p):
        return ((p[0] - (vx + vw * 0.5)) * scale + ICON_CX,
                -(p[1] - (vy + vh * 0.5)) * scale + ICON_CY)

    contours = []
    warnings = []

    for d, style, ctm in items:
        if not ctm.is_conformal():
            warnings.append("非等比变换，描边会略有偏差")
        # 打散精度换算到用户坐标
        s = max(ctm.max_scale() * scale, 1e-9)
        tol_u = tol_user / s

        subs = flatten_path(d, tol_u)
        if not subs:
            continue

        # --- 填充 ---
        fill = (style.get("fill") or "black").strip()
        if fill not in ("none", "transparent") and not fill.startswith("url("):
            rule = (style.get("fill-rule") or "nonzero").strip()
            polys = []
            for sub in subs:
                pts = [ctm.apply(p) for p in sub["points"]]
                if len(pts) >= 3:
                    polys.append(pts)
            if rule == "evenodd":
                # 字体只有 nonzero。evenodd 和它的差别只在嵌套轮廓的绕向上 ——
                # 把奇数层深的轮廓反过来，两种规则就等价了。
                polys = evenodd_to_nonzero(polys)
            contours.extend(to_font_list(polys, to_font))

        # --- 描边 ---
        stroke = (style.get("stroke") or "none").strip()
        if stroke not in ("none", "transparent") and not stroke.startswith("url("):
            width = _num_or(style.get("stroke-width"), 1.0)
            if width > 0:
                cap = (style.get("stroke-linecap") or "butt").strip()
                join = (style.get("stroke-linejoin") or "miter").strip()
                if cap not in ("round", "butt", "square"):
                    warnings.append("未知的线帽 %r，按 butt 处理" % cap)
                    cap = "butt"
                if join not in ("round", "bevel", "miter"):
                    warnings.append("未知的接头 %r，按 bevel 处理" % join)
                    join = "bevel"
                pieces = []
                miter_limit = _num_or(style.get("stroke-miterlimit"), 4.0)
                for sub in subs:
                    pts = [ctm.apply(p) for p in sub["points"]]
                    pieces.extend(expand_stroke(pts, sub["closed"], width,
                                                cap, join, tol_u, miter_limit))
                # 同向化：全部翻成同一个绕向，nonzero 下才是并集
                for piece in pieces:
                    if len(piece) >= 3:
                        contours.append([to_font(p) for p in normalize_winding(piece)])

    return contours, warnings


def normalize_winding(pts):
    """把轮廓翻成统一绕向（这里取逆时针，即用户坐标下面积为正）。"""
    if signed_area(pts) < 0:
        return list(reversed(pts))
    return pts


def to_font_list(polys, to_font):
    return [[to_font(p) for p in poly] for poly in polys]


def near_boundary_point(poly):
    """找一个**紧贴轮廓边界、但在内部**的点。

    evenodd 判嵌套全靠它，而且**不能用重心** —— 这个坑踩过两次：

      外框 (2,2)-(22,22) 的顶点重心是 (12,12)，而内框 (7,7)-(17,17) 的中心
      恰好也是 (12,12)。于是"外框的采样点被内框包住了"→ 外框被判成嵌套一层
      → 反转 → 内外同号 → 洞没了，整个 evenodd 转换白做。

    正确做法是贴着边往内挪一点点。外框边 (2,2)-(22,2) 的中点朝内挪之后是
    (12, 2.002)，稳稳落在内框外面；本来夹着外框的那些轮廓（如果有）才会命中。

    挪进去的那一侧用 point_in_poly 自校验，所以不靠猜。步长从小往大试，
    细长的形状也能取到点。
    """
    n = len(poly)
    span = max(max(p[0] for p in poly) - min(p[0] for p in poly),
               max(p[1] for p in poly) - min(p[1] for p in poly), 1e-9)
    ladder = tuple(span * e for e in (1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 5e-2))

    for i in range(n):
        a = poly[i]
        b = poly[(i + 1) % n]
        dx, dy = b[0] - a[0], b[1] - a[1]
        length = math.hypot(dx, dy)
        if length < 1e-12:
            continue
        nx, ny = -dy / length, dx / length
        mx, my = (a[0] + b[0]) * 0.5, (a[1] + b[1]) * 0.5
        for eps in ladder:
            for side in (1.0, -1.0):
                qx, qy = mx + side * nx * eps, my + side * ny * eps
                if point_in_poly(qx, qy, poly):
                    return (qx, qy)

    # 兜底：实在取不到就用重心（退化形状，判嵌套可能不准，但总比崩强）
    cx = sum(p[0] for p in poly) / n
    cy = sum(p[1] for p in poly) / n
    return (cx, cy)


def evenodd_to_nonzero(polys):
    """把 evenodd 的嵌套轮廓改成 nonzero 能出同样结果的绕向。

    做法：数每个轮廓被别的轮廓套了几层，层数是奇数就把自己反过来。
    这样同一个点落在 k 层里时缠绕数是 +1-1+1…，奇偶正好和 evenodd 一致。
    """
    out = []
    for i, poly in enumerate(polys):
        probe = near_boundary_point(poly)
        depth = 0
        for j, other in enumerate(polys):
            if i != j and point_in_poly(probe[0], probe[1], other):
                depth += 1
        out.append(list(reversed(poly)) if depth % 2 == 1 else poly)
    return out


# ==================================================================== 出字体

def build_font(glyphs_contours, out_path):
    from fontTools.fontBuilder import FontBuilder
    from fontTools.pens.ttGlyphPen import TTGlyphPen

    names = sorted(glyphs_contours.keys())
    cmap = {}
    order = [".notdef"]
    glyf = {}
    metrics = {}

    pen = TTGlyphPen(None)
    glyf[".notdef"] = pen.glyph()
    metrics[".notdef"] = (UPEM, 0)

    mapping = {}
    for idx, name in enumerate(names):
        code = CODEPOINT_BASE + idx
        glyph_name = "uni%04X" % code
        order.append(glyph_name)
        cmap[code] = glyph_name
        mapping[name] = code

        pen = TTGlyphPen(None)
        xs = []
        ys = []
        for contour in glyphs_contours[name]:
            pts = dedupe(contour, eps=0.5)
            if len(pts) < 3:
                continue
            if abs(signed_area(pts)) < 0.5:
                continue
            pen.moveTo((round(pts[0][0]), round(pts[0][1])))
            for p in pts[1:]:
                pen.lineTo((round(p[0]), round(p[1])))
            pen.closePath()
            xs.extend(p[0] for p in pts)
            ys.extend(p[1] for p in pts)
        glyf[glyph_name] = pen.glyph()
        lsb = int(round(min(xs))) if xs else 0
        metrics[glyph_name] = (UPEM, lsb)

    fb = FontBuilder(UPEM, isTTF=True)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyf)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=ASCENDER, descent=DESCENDER)
    fb.setupNameTable({
        "familyName": "AES Icons",
        "styleName": "Regular",
        "uniqueFontIdentifier": "AES Icons Regular",
        "fullName": "AES Icons Regular",
        "psName": "AESIcons-Regular",
        "version": "1.0",
    })
    fb.setupOS2(
        sTypoAscender=ASCENDER, sTypoDescender=DESCENDER, sTypoLineGap=0,
        usWinAscent=ASCENDER, usWinDescent=-DESCENDER,
        # 这两个字段在 OS/2 里是 int16，**必须给整数**，给浮点 fontTools 会直接抛
        sCapHeight=int(ICON_BOX), sxHeight=int(ICON_BOX),
        achVendID="AES ",
        fsType=0,
    )
    fb.setupPost()
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    fb.save(out_path)
    return mapping


def emit_cs(mapping, out_path):
    lines = [
        "// 由 tools/icons/svg2iconfont.py 生成，不要手改。",
        "// 重跑：把 SVG 丢进 tools/icons/svg/ 之后跑那个脚本。",
        "namespace ADOFAIEditorStudio",
        "{",
        "    /// <summary>",
        "    /// 图标字体 UiIcons.ttf 里的码点表。",
        "    ///",
        "    /// 码点落在私有区 E000 往上，和 UiGlyphTable 那张汉字表不重叠。",
        "    /// 拿到码点后用 <c>((char)码点).ToString()</c> 当文本画给 ImGui 就行 ——",
        "    /// 图标在字体里就是字形，和汉字走同一条路。",
        "    /// </summary>",
        "    internal static class UiIconTable",
        "    {",
    ]
    for name in sorted(mapping, key=lambda k: mapping[k]):
        ident = "".join(part.capitalize() for part in re.split(r"[-_. ]+", name) if part)
        if not ident or not ident[0].isalpha():
            ident = "Icon" + ident
        lines.append("        internal const int %s = 0x%04X;   // %s"
                     % (ident, mapping[name], name))
    lines.append("")
    lines.append("        /// <summary>")
    lines.append("        /// 名字 → 码点。**按名字找图标走这里。**")
    lines.append("        ///")
    lines.append("        /// 有这个表，界面代码就不用写死码点 —— 换一套图标集、改几个")
    lines.append("        /// 文件名，界面一行都不用动。")
    lines.append("        /// </summary>")
    lines.append("        private static readonly System.Collections.Generic.Dictionary<string, int> ByName")
    lines.append("            = new System.Collections.Generic.Dictionary<string, int>")
    lines.append("        {")
    for name in sorted(mapping, key=lambda k: mapping[k]):
        lines.append('            { "%s", 0x%04X },' % (name, mapping[name]))
    lines.append("        };")
    lines.append("")
    lines.append("        /// <summary>")
    lines.append("        /// 按名字找码点，找不到返回 0。")
    lines.append("        ///")
    lines.append("        /// **传多个名字是按顺序挑第一个有的。** 同一张图各家图标集叫法不同")
    lines.append("        /// （删除有叫 trash 的也有叫 trash-2 的），这样换一套图标集不用改代码。")
    lines.append("        /// ")
    lines.append("        /// 返回 0 表示没有 —— 码点 0 永远不是合法图标，调用方拿它当\"没有\"。")
    lines.append("        /// </summary>")
    lines.append("        internal static int Find(params string[] names)")
    lines.append("        {")
    lines.append("            if (names == null)")
    lines.append("            {")
    lines.append("                return 0;")
    lines.append("            }")
    lines.append("            for (int i = 0; i < names.Length; i++)")
    lines.append("            {")
    lines.append("                int code;")
    lines.append("                if (names[i] != null && ByName.TryGetValue(names[i], out code))")
    lines.append("                {")
    lines.append("                    return code;")
    lines.append("                }")
    lines.append("            }")
    lines.append("            return 0;")
    lines.append("        }")
    lines.append("    }")
    lines.append("}")
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def emit_preview(font_path, mapping, out_path, px=64, cols=8):
    """把成品字体排成一张图，肉眼核对形状。**不是可选步骤** ——
    字形对不对只能看，看不出来就是没验。"""
    from PIL import Image, ImageDraw, ImageFont

    names = sorted(mapping, key=lambda k: mapping[k])
    rows = max(1, (len(names) + cols - 1) // cols)
    cell_w, cell_h = px + 48, px + 40
    img = Image.new("RGB", (cols * cell_w, rows * cell_h), (24, 24, 28))
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype(font_path, px)
        label = ImageFont.load_default()
    except Exception as exc:
        print("[预览] 加载字体失败：" + str(exc))
        return

    for i, name in enumerate(names):
        cx = (i % cols) * cell_w
        cy = (i // cols) * cell_h
        draw.rectangle([cx + 2, cy + 2, cx + cell_w - 4, cy + cell_h - 4],
                       outline=(60, 60, 68))
        ch = chr(mapping[name])
        draw.text((cx + cell_w // 2, cy + 8 + px // 2), ch,
                  font=font, fill=(235, 235, 240), anchor="mm")
        draw.text((cx + cell_w // 2, cy + cell_h - 16), name,
                  font=label, fill=(140, 140, 150), anchor="mm")
    img.save(out_path)
    print("[预览] " + out_path)


# ==================================================================== 自检样例

SELFTEST_SVGS = {
    # 描边 + 圆帽 + 圆接头（Lucide 那一族的画法）
    "stroke_eye": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
        'stroke="black" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
        '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/>'
        '<circle cx="12" cy="12" r="3"/></svg>'),
    # 填充（nonzero）
    "fill_square": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">'
        '<path d="M3 3H21V21H3Z"/></svg>'),
    # 填充 + 洞（两个同向的矩形，nonzero 下内圈是洞要看成实心 —— 这里测嵌套）
    "fill_ring_nonzero": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">'
        '<path d="M2 2H22V22H2Z M7 7H17V17H7Z"/></svg>'),
    # evenodd 的洞（和上面画法相同但靠 fill-rule 出洞）
    "fill_ring_evenodd": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill-rule="evenodd">'
        '<path d="M2 2H22V22H2Z M7 7H17V17H7Z"/></svg>'),
    # 圆弧命令 + 尖角
    "arc_pie": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
        'stroke="black" stroke-width="2" stroke-linecap="round">'
        '<path d="M12 12 L20 6 A10 10 0 0 1 20 18 Z"/></svg>'),
    # 同一个图形换成圆接头 —— 和上面那张对着看，接头这一块就验完了
    "arc_pie_round": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
        'stroke="black" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
        '<path d="M12 12 L20 6 A10 10 0 0 1 20 18 Z"/></svg>'),
    # 直角 + 斜接（miter），看尖角对不对
    "corner_miter": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
        'stroke="black" stroke-width="3" stroke-linecap="butt" stroke-linejoin="miter">'
        '<path d="M4 20 V6 H20"/></svg>'),
    # 各种图元 + 变换
    "primitives": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">'
        '<rect x="2" y="2" width="8" height="8" rx="2"/>'
        '<g transform="translate(12 0)"><circle cx="6" cy="6" r="4"/></g>'
        '<g transform="translate(0 12) scale(0.5)">'
        '<polygon points="4,0 8,8 0,8"/></g>'
        '<g transform="translate(12 12) rotate(45)">'
        '<line x1="0" y1="0" x2="8" y2="8" stroke="black" stroke-width="2" stroke-linecap="round"/>'
        '</g>'
        '<ellipse cx="6" cy="18" rx="4" ry="2"/></svg>'),
    # 相对命令 + 平滑曲线（miter 接头）
    "rel_smooth": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
        'stroke="black" stroke-width="2" stroke-linecap="round">'
        '<path d="m4 18 c0-8 4-8 4 0 s4 8 4 0 s4-8 4 0"/></svg>'),
    # 同一条曲线换成圆接头。**和上面那张对着看** —— 毛刺是接头那块的锅还是
    # 曲线打散的锅，一比就出来了。
    "rel_smooth_round": (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
        'stroke="black" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
        '<path d="m4 18 c0-8 4-8 4 0 s4 8 4 0 s4-8 4 0"/></svg>'),
}


def run_selftest():
    import tempfile
    tmp = tempfile.mkdtemp(prefix="aes_icons_")
    print("[自检] 样例目录 " + tmp)
    glyphs = {}
    for name, svg in SELFTEST_SVGS.items():
        p = os.path.join(tmp, name + ".svg")
        with open(p, "w", encoding="utf-8") as f:
            f.write(svg)
        contours, warns = build_contours(p, FLATTEN_TOL)
        for w in warns:
            print("[自检] %s 警告：%s" % (name, w))
        npts = sum(len(c) for c in contours)
        print("[自检] %-20s 轮廓 %2d 个，共 %5d 点" % (name, len(contours), npts))
        # 每个轮廓的绕向要打出来 —— evenodd 那两个样例会画出同样的外形，
        # 差别**只在绕向**上，光看张数看不出来。
        areas = [signed_area(c) for c in contours]
        pos = sum(1 for a in areas if a > 0)
        print("       绕向：正 %d 负 %d   面积 %s"
              % (pos, len(areas) - pos,
                 " ".join("%.0f" % a for a in sorted(areas, key=abs, reverse=True)[:6])))
        glyphs[name] = contours

    out = os.path.join(tmp, "selftest.ttf")
    mapping = build_font(glyphs, out)
    print("[自检] TTF " + out + "  " + str(os.path.getsize(out)) + " 字节")
    preview = os.path.join(tmp, "selftest.png")
    emit_preview(out, mapping, preview, px=180, cols=4)
    print("[自检] 预览 " + preview)


# ==================================================================== 主流程

def main():
    ap = argparse.ArgumentParser(description="SVG 图标 → TTF 图标字体")
    ap.add_argument("--svg-dir", default=SVG_DIR, help="SVG 目录（默认 tools/icons/svg）")
    ap.add_argument("--out", default=OUT_FONT, help="输出的 TTF 路径")
    ap.add_argument("--cs", default=OUT_CS, help="输出的 C# 码点表路径")
    ap.add_argument("--json", default=OUT_JSON, help="输出的 name→码点 JSON")
    ap.add_argument("--preview", default=OUT_PREVIEW, help="输出的预览图路径")
    ap.add_argument("--no-preview", action="store_true", help="跳过预览图")
    ap.add_argument("--preview-px", type=int, default=64, help="预览图里图标的光栅尺寸")
    ap.add_argument("--preview-cols", type=int, default=8, help="预览图列数")
    ap.add_argument("--selftest", action="store_true", help="跑内置样例，不读 svg 目录")
    args = ap.parse_args()

    if args.selftest:
        run_selftest()
        return

    # 目录不存在就建一个 —— 第一次跑不该只丢一句"找不到"然后退出
    if not os.path.isdir(args.svg_dir):
        os.makedirs(args.svg_dir, exist_ok=True)
        print("建好了目录：" + args.svg_dir)
        print("把 .svg 丢进去（文件名就是图标名），再跑一次。")
        sys.exit(1)

    files = sorted(f for f in os.listdir(args.svg_dir) if f.lower().endswith(".svg"))
    if not files:
        print("目录里还没有 .svg：" + args.svg_dir)
        print("把 .svg 丢进去（文件名就是图标名），再跑一次。")

    glyphs = {}
    failed = []
    for fname in files:
        name = os.path.splitext(fname)[0]
        path = os.path.join(args.svg_dir, fname)
        try:
            contours, warns = build_contours(path, FLATTEN_TOL)
        except Exception as exc:
            failed.append((fname, str(exc)))
            continue
        for w in warns:
            print("  ! %s：%s" % (fname, w))
        if not contours:
            failed.append((fname, "没解析出任何图形"))
            continue
        glyphs[name] = contours
        print("  %-24s 轮廓 %3d 个，共 %6d 点"
              % (name, len(contours), sum(len(c) for c in contours)))

    if failed:
        print("\n有 %d 个没转成功：" % len(failed))
        for fname, why in failed:
            print("  " + fname + "：" + why)

    # **一个图标都没有也要出那份 C# 码点表。**
    #
    # 桥是拿 `typeof(UiIconTable)` 来建字形范围的（走反射，见 BuildIconGlyphRange），
    # 所以这个类型不在工程就编译不过。空表至少编译得过，运行时图标那部分自动降级。
    if not glyphs:
        emit_cs({}, args.cs)
        with open(args.json, "w", encoding="utf-8") as f:
            f.write("{}\n")
        print("\n没有可用图标，只写了一份空的码点表：" + args.cs)
        print("（没动字体 —— 上一版 UiIcons.ttf 原样留着）")
        return

    mapping = build_font(glyphs, args.out)
    with open(args.json, "w", encoding="utf-8") as f:
        json.dump(mapping, f, ensure_ascii=False, indent=2, sort_keys=True)
        f.write("\n")
    emit_cs(mapping, args.cs)

    print("\n字体   " + args.out + "  " + str(os.path.getsize(args.out)) + " 字节")
    print("码点表 " + args.cs)
    print("映射   " + args.json)
    if not args.no_preview:
        emit_preview(args.out, mapping, args.preview,
                     px=args.preview_px, cols=args.preview_cols)


if __name__ == "__main__":
    main()
