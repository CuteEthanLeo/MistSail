# -*- coding: utf-8 -*-
"""雾帆 MistSail 介绍视频渲染器：Pillow 逐帧绘制 -> ffmpeg(libx264) 分段编码 -> 合并 + 配乐。"""
import math, os, re, subprocess, sys
from multiprocessing import Pool
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W, H = 1920, 1080
FPS = 60
DURATION = 63.0
TOTAL = int(DURATION * FPS)
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
FFMPEG = None

# ---------- 颜色 / 字体 ----------
BG = (11, 15, 26)
CYAN = (56, 189, 248)
VIOLET = (167, 139, 250)
GREEN = (52, 211, 153)
RED = (248, 113, 113)
AMBER = (251, 191, 36)
WHITE = (240, 244, 255)
GREY = (148, 163, 184)
PANEL = (20, 26, 42)
PANEL2 = (28, 36, 58)

FONT_DIR = "C:/Windows/Fonts/"
_font_cache = {}


def font(name, size):
    key = (name, size)
    if key not in _font_cache:
        path = {
            "bold": FONT_DIR + "msyhbd.ttc",
            "reg": FONT_DIR + "msyh.ttc",
            "light": FONT_DIR + "msyhl.ttc",
            "mono": FONT_DIR + "CascadiaMono.ttf",
            "hei": FONT_DIR + "simhei.ttf",
        }[name]
        _font_cache[key] = ImageFont.truetype(path, size)
    return _font_cache[key]


# ---------- 缓动 ----------
def clamp(x, a=0.0, b=1.0):
    return max(a, min(b, x))


def ease_out(t):
    t = clamp(t)
    return 1 - (1 - t) ** 3


def ease_in_out(t):
    t = clamp(t)
    return t * t * (3 - 2 * t)


def ease_out_back(t):
    t = clamp(t)
    c1, c3 = 1.70158, 2.70158
    return 1 + c3 * (t - 1) ** 3 + c1 * (t - 1) ** 2


def lerp(a, b, t):
    return a + (b - a) * t


def lerp_c(c1, c2, t):
    return tuple(int(round(lerp(a, b, t))) for a, b in zip(c1, c2))


def with_a(c, a):
    return (c[0], c[1], c[2], int(round(255 * clamp(a))))


def seg(t, start, dur):
    """0..1 进度，t 相对 start"""
    return clamp((t - start) / dur) if dur > 0 else 1.0


# ---------- 基础绘制 ----------
def text_w(draw, s, f):
    return draw.textlength(s, font=f)


def draw_text(draw, xy, s, f, fill, anchor="la"):
    draw.text(xy, s, font=f, fill=fill, anchor=anchor)


def rrect(draw, box, r, fill=None, outline=None, width=1):
    draw.rounded_rectangle(box, radius=r, fill=fill, outline=outline, width=width)


def glow_sprite(radius, color, alpha=1.0):
    size = radius * 4
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((radius, radius, 3 * radius, 3 * radius), fill=with_a(color, alpha))
    return img.filter(ImageFilter.GaussianBlur(radius * 0.9))


_glow_cache = {}


def paste_glow(layer, center, radius, color, alpha):
    if alpha <= 0.01:
        return
    key = (radius, color)
    if key not in _glow_cache:
        _glow_cache[key] = glow_sprite(radius, color, 1.0)
    spr = _glow_cache[key]
    if alpha < 1:
        a = spr.getchannel("A").point(lambda v: int(v * alpha))
        spr = spr.copy()
        spr.putalpha(a)
    x, y = int(center[0] - spr.width / 2), int(center[1] - spr.height / 2)
    layer.alpha_composite(spr, (x, y))


def make_background():
    base = np.zeros((H, W, 3), dtype=np.float32)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    # 左上冷蓝到右下深紫的渐变
    g = (xx / W) * 0.6 + (yy / H) * 0.4
    c1 = np.array([10, 14, 26], dtype=np.float32)
    c2 = np.array([16, 12, 32], dtype=np.float32)
    base[:] = c1[None, None, :] * (1 - g[..., None]) + c2[None, None, :] * g[..., None]
    # 两团柔光
    for cx, cy, col, r in [(0.2, 0.25, (56, 189, 248), 0.55), (0.85, 0.8, (167, 139, 250), 0.6)]:
        dist = np.sqrt(((xx / W - cx) ** 2) + ((yy / H - cy) * (H / W)) ** 2)
        k = np.clip(1 - dist / r, 0, 1) ** 2 * 0.16
        base += np.array(col, dtype=np.float32)[None, None, :] * k[..., None]
    img = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8), "RGB")
    # 网格
    grid = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(grid)
    for x in range(0, W, 80):
        d.line((x, 0, x, H), fill=(255, 255, 255, 9), width=1)
    for y in range(0, H, 80):
        d.line((0, y, W, y), fill=(255, 255, 255, 9), width=1)
    img = Image.alpha_composite(img.convert("RGBA"), grid)
    # 暗角
    vig = np.sqrt(((xx / W - 0.5) * 2) ** 2 + ((yy / H - 0.5) * 2) ** 2)
    vig = np.clip(1 - (vig - 0.75) * 0.55, 0.55, 1.0)
    arr = np.asarray(img).astype(np.float32)
    arr[..., :3] *= vig[..., None]
    return Image.fromarray(arr.astype(np.uint8), "RGBA")


# ---------- 公共元素 ----------
def draw_check(d, cx, cy, size, fill, width=4):
    d.line([(cx - size * 0.55, cy), (cx - size * 0.15, cy + size * 0.4), (cx + size * 0.6, cy - size * 0.45)], fill=fill, width=width, joint="curve")


def draw_tri(d, cx, cy, r, fill=None, outline=None, width=3):
    pts = [(cx - r * 0.9, cy), (cx + r * 0.75, cy - r * 0.9), (cx + r * 0.75, cy + r * 0.9)]
    d.polygon(pts, fill=fill, outline=outline)
    if outline:
        d.line(pts + [pts[0]], fill=outline, width=width)


def draw_browser(d, x, y, w, h, alpha, label="浏览器"):
    rrect(d, (x, y, x + w, y + h), 18, fill=with_a(PANEL2, alpha), outline=with_a(GREY, alpha * 0.6), width=2)
    rrect(d, (x, y, x + w, y + 44), 18, fill=with_a((40, 50, 78), alpha))
    d.rectangle((x, y + 30, x + w, y + 44), fill=with_a((40, 50, 78), alpha))
    for i, c in enumerate([RED, AMBER, GREEN]):
        d.ellipse((x + 18 + i * 26, y + 14, x + 34 + i * 26, y + 30), fill=with_a(c, alpha))
    rrect(d, (x + 110, y + 10, x + w - 20, y + 34), 8, fill=with_a((18, 22, 36), alpha))
    d.text((x + 124, y + 22), "https://example.com", font=font("mono", 16), fill=with_a(GREY, alpha), anchor="lm")
    # 页面线条
    for i in range(4):
        ww = [0.7, 0.9, 0.5, 0.8][i]
        rrect(d, (x + 24, y + 70 + i * 30, x + 24 + (w - 48) * ww, y + 84 + i * 30), 6, fill=with_a((48, 58, 90), alpha))
    d.text((x + w / 2, y + h + 30), label, font=font("reg", 26), fill=with_a(WHITE, alpha), anchor="mm")


def draw_server(d, x, y, w, h, alpha, label="真实服务器", ok=None):
    rrect(d, (x, y, x + w, y + h), 18, fill=with_a(PANEL2, alpha), outline=with_a(GREY, alpha * 0.6), width=2)
    for i in range(3):
        yy = y + 22 + i * ((h - 44) / 3)
        rrect(d, (x + 20, yy, x + w - 20, yy + (h - 44) / 3 - 12), 8, fill=with_a((40, 50, 78), alpha))
        d.ellipse((x + w - 50, yy + 10, x + w - 36, yy + 24), fill=with_a(GREEN if ok is not False else RED, alpha))
        for k in range(5):
            d.line((x + 36 + k * 18, yy + 12, x + 36 + k * 18, yy + 22), fill=with_a(GREY, alpha * 0.7), width=3)
    d.text((x + w / 2, y + h + 30), label, font=font("reg", 26), fill=with_a(WHITE, alpha), anchor="mm")


def draw_eye(layer, d, cx, cy, alpha, alert, size=70):
    """中间设备：一只眼睛；alert 0..1 发红"""
    col = lerp_c(GREY, RED, alert)
    paste_glow(layer, (cx, cy), 90, RED, alpha * alert * 0.8)
    pts = []
    for i in range(41):
        a = i / 40
        pts.append((cx - size + 2 * size * a, cy - math.sin(a * math.pi) * size * 0.55))
    for i in range(41):
        a = 1 - i / 40
        pts.append((cx - size + 2 * size * a, cy + math.sin(a * math.pi) * size * 0.55))
    d.polygon(pts, fill=with_a(PANEL2, alpha), outline=with_a(col, alpha))
    d.line(pts + [pts[0]], fill=with_a(col, alpha), width=4)
    r = size * 0.3
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=with_a(col, alpha))
    r2 = r * 0.5
    d.ellipse((cx - r2, cy - r2, cx + r2, cy + r2), fill=with_a((15, 18, 30), alpha))


def draw_pill(d, cx, cy, segments, alpha, f=None, pad=18, h=54, fill=PANEL2, outline=GREY):
    """segments: [(text, color)]，整体居中"""
    f = f or font("mono", 24)
    widths = [text_w(d, s, f) for s, _ in segments]
    tw = sum(widths)
    w = tw + pad * 2
    x0 = cx - w / 2
    rrect(d, (x0, cy - h / 2, x0 + w, cy + h / 2), h / 2, fill=with_a(fill, alpha), outline=with_a(outline, alpha * 0.8), width=2)
    x = x0 + pad
    for (s, c), ww in zip(segments, widths):
        d.text((x, cy), s, font=f, fill=with_a(c, alpha), anchor="lm")
        x += ww
    return w


def draw_wire(d, x1, x2, y, alpha, color=GREY, dash=True, phase=0.0):
    if not dash:
        d.line((x1, y, x2, y), fill=with_a(color, alpha), width=3)
        return
    step = 28
    off = (phase * step) % step
    x = x1 - off
    while x < x2:
        a, b = max(x1, x), min(x2, x + 14)
        if b > a:
            d.line((a, y, b, y), fill=with_a(color, alpha), width=3)
        x += step


def draw_heading(d, text, y, alpha, size=56, color=WHITE, sub=None, slide=0.0):
    f = font("bold", size)
    d.text((W / 2, y + slide), text, font=f, fill=with_a(color, alpha), anchor="mm")
    if sub:
        d.text((W / 2, y + slide + size * 0.95), sub, font=font("light", int(size * 0.5)), fill=with_a(GREY, alpha), anchor="mm")


# ---------- 代码高亮 ----------
CS_KW = set("if else while return await int byte bool out var new null true false async using namespace class internal public private sealed override static readonly string checked ushort is".split())
TOKEN_RE = re.compile(r'(//.*$)|("(?:[^"\\]|\\.)*")|(\b\d+\b)|(\b[A-Za-z_][A-Za-z0-9_]*\b)|(\S)|(\s+)')


def tokenize(line):
    out = []
    for m in TOKEN_RE.finditer(line):
        s = m.group(0)
        if m.group(1):
            c = (100, 116, 139)
        elif m.group(2):
            c = (253, 186, 116)
        elif m.group(3):
            c = (190, 242, 100)
        elif m.group(4):
            if s in CS_KW:
                c = (196, 141, 255)
            elif s[0].isupper():
                c = (94, 234, 212)
            else:
                c = (226, 232, 240)
        else:
            c = (148, 163, 184)
        out.append((s, c))
    return out


CODE_LINES = [
    "if (!TryFindServerName(data.AsSpan(0, recordLength),",
    "                       out int nameOffset, out int nameLength))",
    "    await inner.WriteAsync(data.AsMemory(0, recordLength), ct);",
    "else",
    "{",
    "    int start = options.FragmentMode == \"tcp\" ? 0 : 5;",
    "    // 域名绝不完整出现在同一个 record 里：短域名也会被从中间切开",
    "    int nameChunk = Math.Min(options.FragmentBytes, Math.Max(1, (nameLength + 1) / 2));",
    "    int nameEnd = nameOffset + nameLength;",
    "    while (start < recordLength)",
    "    {",
    "        int end = start < nameOffset ? nameOffset :",
    "            start < nameEnd ? Math.Min(start + nameChunk, nameEnd) : recordLength;",
    "        if (options.FragmentMode == \"tls-record\")",
    "        {",
    "            byte[] header = [data[0], data[1], data[2], 0, 0];",
    "            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3), checked((ushort)(end - start)));",
    "            await inner.WriteAsync(header, ct);",
    "        }",
    "        await inner.WriteAsync(data.AsMemory(start, end - start), ct);",
    "        start = end;",
    "        if (start < recordLength && options.FragmentDelayMs > 0)",
    "            await Task.Delay(options.FragmentDelayMs, ct);",
    "    }",
]
CODE_TOKENS = [tokenize(l) for l in CODE_LINES]

JSON_LINES = [
    ('{', GREY),
    ('  "FragmentMode":    "tls-record",', None),
    ('  "FragmentBytes":   8,', None),
    ('  "FragmentDelayMs": 8,', None),
    ('  "RelayListenAddress": "127.0.0.2",', None),
    ('  "DohServers": [ "cloudflare-dns.com", "dns.quad9.net", "dns.google" ],', None),
    ('  "EnableIPv6": false', None),
    ('}', GREY),
]


def json_tokens(line):
    out = []
    for m in re.finditer(r'("[^"]*")(\s*:)?|(\d+)|(true|false)|(\S)|(\s+)', line):
        s = m.group(0)
        if m.group(1):
            if m.group(2):
                out.append((m.group(1), (125, 211, 252)))
                out.append((m.group(2), GREY))
                continue
            c = (253, 186, 116)
        elif m.group(3):
            c = (190, 242, 100)
        elif m.group(4):
            c = (196, 141, 255)
        else:
            c = GREY
        out.append((s, c))
    return out


def draw_code_window(d, layer, x, y, w, h, alpha, title, tokens_lines, chars_visible, f, line_h, highlight_lines=(), hl_alpha=0.0, cursor_on=True):
    rrect(d, (x, y, x + w, y + h), 16, fill=with_a((13, 17, 30), alpha * 0.96), outline=with_a((60, 72, 110), alpha), width=2)
    rrect(d, (x, y, x + w, y + 46), 16, fill=with_a((24, 30, 48), alpha))
    d.rectangle((x, y + 30, x + w, y + 46), fill=with_a((24, 30, 48), alpha))
    for i, c in enumerate([RED, AMBER, GREEN]):
        d.ellipse((x + 18 + i * 26, y + 15, x + 34 + i * 26, y + 31), fill=with_a(c, alpha))
    d.text((x + w / 2, y + 23), title, font=font("mono", 18), fill=with_a(GREY, alpha), anchor="mm")
    cy = y + 70
    remaining = chars_visible
    cursor_pos = None
    for li, toks in enumerate(tokens_lines):
        if li in highlight_lines and hl_alpha > 0:
            rrect(d, (x + 8, cy - 4, x + w - 8, cy + line_h - 6), 6, fill=with_a(lerp_c((13, 17, 30), (56, 189, 248), hl_alpha * 0.14), alpha * 0.96))
        d.text((x + 18, cy), f"{li + 1:>2}", font=f, fill=with_a((71, 85, 105), alpha), anchor="la")
        cx = x + 70
        line_done = True
        for s, c in toks:
            if remaining <= 0:
                line_done = False
                break
            if len(s) > remaining:
                s = s[:remaining]
                line_done = False
            remaining -= len(s)
            ff = f if s.isascii() else font("reg", f.size)
            d.text((cx, cy), s, font=ff, fill=with_a(c, alpha), anchor="la")
            cx += text_w(d, s, ff)
            if not line_done:
                break
        if not line_done or remaining <= 0:
            if cursor_pos is None:
                cursor_pos = (cx, cy)
            if remaining <= 0 and not line_done:
                break
            if remaining <= 0:
                break
        cy += line_h
    if cursor_pos and cursor_on:
        d.rectangle((cursor_pos[0] + 2, cursor_pos[1] + 2, cursor_pos[0] + 12, cursor_pos[1] + line_h - 8), fill=with_a(CYAN, alpha))


# ---------- 场景 ----------
def scene_intro(layer, d, t, dur):
    cx, cy = W / 2, H / 2 - 40
    a0 = ease_out(seg(t, 0.0, 1.0))
    # 环形光晕脉动
    pulse = 0.5 + 0.5 * math.sin(t * 2.2)
    paste_glow(layer, (cx, cy - 40), 260, CYAN, 0.35 * a0 * (0.75 + 0.25 * pulse))
    paste_glow(layer, (cx + 120, cy + 40), 220, VIOLET, 0.25 * a0)
    # 三角形 logo（◁）由线条绘出
    k = ease_out(seg(t, 0.2, 1.1))
    r = 110
    pts = [(cx - r * 0.9, cy - 60), (cx + r * 0.75, cy - 60 - r * 0.9), (cx + r * 0.75, cy - 60 + r * 0.9)]
    total = 3
    n = k * total
    for i in range(3):
        p1, p2 = pts[i], pts[(i + 1) % 3]
        frac = clamp(n - i)
        if frac <= 0:
            continue
        q = (lerp(p1[0], p2[0], frac), lerp(p1[1], p2[1], frac))
        d.line((p1, q), fill=with_a(CYAN, a0), width=10)
    if k >= 1:
        fill_a = ease_out(seg(t, 1.3, 0.8))
        d.polygon(pts, fill=with_a(CYAN, fill_a * 0.22))
    # 标题
    ta = ease_out(seg(t, 0.9, 1.0))
    slide = (1 - ta) * 40
    f = font("bold", 130)
    d.text((cx - 150, cy + 105 + slide), "雾帆", font=f, fill=with_a(WHITE, ta), anchor="mm")
    d.text((cx + 150, cy + 112 + slide), "MistSail", font=font("light", 96), fill=with_a(CYAN, ta), anchor="mm")
    sa = ease_out(seg(t, 1.6, 0.9))
    d.text((cx, cy + 210 + (1 - sa) * 30), "— 让握手藏进雾里 —", font=font("light", 40), fill=with_a(GREY, sa), anchor="mm")
    # 标签
    tags = ["WPF · .NET 8", "开源", "无代理服务器", "本机接管握手"]
    for i, tg in enumerate(tags):
        ka = ease_out_back(seg(t, 2.3 + i * 0.18, 0.7))
        if ka <= 0:
            continue
        fx = font("reg", 26)
        tw = text_w(d, tg, fx) + 44
        total_w = sum(text_w(d, s, fx) + 44 for s in tags) + 24 * (len(tags) - 1)
        x0 = cx - total_w / 2 + sum(text_w(d, s, fx) + 44 + 24 for s in tags[:i])
        y0 = cy + 300
        sc = 0.6 + 0.4 * ka
        hw, hh = tw / 2 * sc, 26 * sc
        mx = x0 + tw / 2
        rrect(d, (mx - hw, y0 - hh, mx + hw, y0 + hh), hh, fill=with_a((30, 40, 66), ka), outline=with_a(CYAN, ka * 0.7), width=2)
        if ka > 0.6:
            d.text((mx, y0), tg, font=fx, fill=with_a(WHITE, (ka - 0.6) / 0.4), anchor="mm")


def scene_problem(layer, d, t, dur):
    a0 = ease_out(seg(t, 0.0, 0.8))
    draw_heading(d, "HTTPS 加密了内容，却没有加密 SNI", 150, a0, 60, slide=(1 - a0) * -30)
    sub_a = ease_out(seg(t, 0.6, 0.8))
    d.text((W / 2, 225), "TLS 握手的第一个数据包 ClientHello 里，Server Name Indication 以明文写着目标域名", font=font("light", 30), fill=with_a(GREY, sub_a), anchor="mm")
    by = 520
    draw_browser(d, 180, by - 110, 340, 220, a0)
    draw_server(d, W - 180 - 300, by - 110, 300, 220, a0)
    x1, x2 = 540, W - 500
    draw_wire(d, x1, x2, by, a0 * 0.6, phase=t * 1.5)
    # 眼睛
    ex, ey = W / 2, by - 160
    alert = 0.0
    # 包从浏览器到服务器 (两次循环)
    cycle = 4.0
    tt = (t - 1.2)
    if tt > 0:
        ph = (tt % cycle) / cycle
        prog = ease_in_out(clamp(ph / 0.7))
        px = lerp(x1 + 60, x2 - 60, prog)
        near = 1 - clamp(abs(px - ex) / 260)
        alert = near
        if ph < 0.7:
            pa = min(1, ph * 8, (0.7 - ph) * 8)
            w = draw_pill(d, px, by, [("ClientHello  ", GREY), ("SNI: ", WHITE), ("example.com", RED)], pa * a0, f=font("mono", 26))
            # 从眼睛到包的视线
            if near > 0.05:
                d.line((ex, ey + 40, px, by - 30), fill=with_a(RED, near * 0.6 * a0), width=2)
    eye_a = ease_out(seg(t, 0.5, 0.8))
    draw_eye(layer, d, ex, ey, eye_a, alert)
    d.text((ex, ey - 90), "链路上的中间设备", font=font("reg", 26), fill=with_a(GREY, eye_a), anchor="mm")
    # 放大的明文标签
    la = ease_out(seg(t, 3.2, 0.8))
    if la > 0:
        yb = by + 200
        rrect(d, (W / 2 - 520, yb - 50, W / 2 + 520, yb + 50), 20, fill=with_a((40, 20, 30), la * 0.9), outline=with_a(RED, la * 0.8), width=2)
        d.text((W / 2, yb), "明文  ·  未加密  ·  任何中间设备都能读到你要访问的域名", font=font("reg", 32), fill=with_a((254, 202, 202), la), anchor="mm")


def scene_block(layer, d, t, dur):
    a0 = ease_out(seg(t, 0.0, 0.8))
    draw_heading(d, "于是「你在访问谁」成了公开信息", 150, a0, 60, slide=(1 - a0) * -30)
    d.text((W / 2, 225), "中间设备据此嗅探、劫持，甚至直接重置连接", font=font("light", 30), fill=with_a(GREY, ease_out(seg(t, 0.5, 0.8))), anchor="mm")
    by = 520
    draw_browser(d, 180, by - 110, 340, 220, a0)
    x1, x2 = 540, W - 500
    ex, ey = W / 2, by - 160
    # 包飞到中间被截断
    tt = t - 0.8
    prog = ease_in_out(clamp(tt / 1.6))
    px = lerp(x1 + 60, ex, prog)
    hit = seg(t, 2.4, 0.5)
    draw_wire(d, x1, ex - 40, by, a0 * 0.6, phase=t * 1.5)
    draw_wire(d, ex + 40, x2, by, a0 * 0.25, color=RED if hit > 0 else GREY, phase=t * 1.5)
    draw_server(d, W - 180 - 300, by - 110, 300, 220, a0 * (1 - 0.5 * hit), ok=None if hit == 0 else False)
    alert = clamp(prog * 1.2)
    if tt > 0 and hit < 1:
        pa = 1 - hit
        shake = math.sin(t * 60) * 6 * hit
        draw_pill(d, px + shake, by, [("ClientHello  ", GREY), ("SNI: ", WHITE), ("example.com", RED)], pa * a0, f=font("mono", 26))
    draw_eye(layer, d, ex, ey, a0, alert)
    d.text((ex, ey - 90), "链路上的中间设备", font=font("reg", 26), fill=with_a(GREY, a0), anchor="mm")
    # RST 闪现 + 碎片
    if hit > 0:
        ka = ease_out_back(hit)
        f = font("bold", 64)
        sc = 0.7 + 0.3 * ka
        paste_glow(layer, (ex, by), 160, RED, 0.7 * min(1, hit * 2) * (1 - 0.3 * seg(t, 3.5, 1)))
        d.text((ex, by + 4), "连接被重置", font=font("bold", int(56 * sc)), fill=with_a(WHITE, min(1, hit * 2)), anchor="mm")
        d.text((ex, by + 70), "TCP RST  /  TLS alert  /  劫持到别处", font=font("reg", 24), fill=with_a(RED, ease_out(seg(t, 2.9, 0.6))), anchor="mm")
        # 碎片飞散
        rng = np.random.RandomState(7)
        for i in range(18):
            ang = rng.uniform(0, 2 * math.pi)
            sp = rng.uniform(120, 360)
            dt = clamp((t - 2.4) / 1.4)
            r = sp * ease_out(dt)
            fa = (1 - dt) * a0
            if fa > 0:
                qx, qy = ex + math.cos(ang) * r, by + math.sin(ang) * r * 0.6
                s = 6 * (1 - dt) + 2
                d.rectangle((qx - s, qy - s, qx + s, qy + s), fill=with_a(RED, fa))
    # 底部结论
    la = ease_out(seg(t, 4.3, 0.8))
    if la > 0:
        yb = by + 210
        items = [("内容", "已加密", GREEN), ("目标域名", "明文可见", RED), ("连接", "可被识别与干预", RED)]
        total_w = 3 * 360 + 2 * 40
        x = W / 2 - total_w / 2
        for i, (k, v, c) in enumerate(items):
            ia = ease_out(seg(t, 4.3 + i * 0.2, 0.7))
            rrect(d, (x, yb - 55, x + 360, yb + 55), 18, fill=with_a(PANEL, ia * 0.95), outline=with_a(c, ia * 0.7), width=2)
            d.text((x + 180, yb - 18), k, font=font("reg", 26), fill=with_a(GREY, ia), anchor="mm")
            d.text((x + 180, yb + 22), v, font=font("bold", 30), fill=with_a(c, ia), anchor="mm")
            x += 400


def scene_solution(layer, d, t, dur):
    a0 = ease_out(seg(t, 0.0, 0.8))
    draw_heading(d, "雾帆的做法：不加代理，在本机接管握手", 140, a0, 56, slide=(1 - a0) * -30)
    d.text((W / 2, 212), "hosts 把域名指向本机 → 常规站点由本机 nginx 改写 SNI 后连向真实服务器 → Cloudflare 类站点改走回环中继，把 ClientHello 切片透传", font=font("light", 26), fill=with_a(GREY, ease_out(seg(t, 0.6, 0.8))), anchor="mm")
    by = 540
    bx, bw = 120, 300
    draw_browser(d, bx, by - 100, bw, 200, a0)
    # 中继盒
    ra = ease_out_back(seg(t, 0.8, 0.9))
    rx, rw = 620, 300
    if ra > 0:
        sc = 0.7 + 0.3 * ra
        hw, hh = rw / 2 * sc, 100 * sc
        mx = rx + rw / 2
        paste_glow(layer, (mx, by), 180, CYAN, 0.35 * ra)
        rrect(d, (mx - hw, by - hh, mx + hw, by + hh), 20, fill=with_a((18, 40, 60), ra), outline=with_a(CYAN, ra), width=3)
        if ra > 0.5:
            fa = (ra - 0.5) / 0.5
            d.text((mx, by - 40), "雾帆 MistSail", font=font("bold", 30), fill=with_a(WHITE, fa), anchor="mm")
            d.text((mx, by + 2), "本机接管 · 改写或分片 SNI", font=font("reg", 24), fill=with_a(CYAN, fa), anchor="mm")
            d.text((mx, by + 44), "127.0.0.1 / 127.0.0.2 :443", font=font("mono", 22), fill=with_a(GREY, fa), anchor="mm")
        d.text((mx, by + 130), "hosts: example.com → 127.0.0.2", font=font("mono", 22), fill=with_a(GREY, ra), anchor="mm")
    sx, sw = W - 120 - 300, 300
    draw_server(d, sx, by - 100, sw, 200, a0)
    ex, ey = 1270, by - 190
    draw_wire(d, bx + bw, rx, by, a0 * 0.6, phase=t * 1.5)
    draw_wire(d, rx + rw, sx, by, a0 * 0.6, phase=t * 1.5)
    # 阶段 1：完整包 浏览器 -> 中继 (t 1.8..3.2)
    p1 = ease_in_out(seg(t, 1.8, 1.4))
    if 0 < p1 < 1 or (p1 >= 1 and t < 3.3):
        px = lerp(bx + bw + 80, rx - 60, p1)
        fade = 1 if p1 < 1 else clamp(1 - (t - 3.2) / 0.1)
        draw_pill(d, px, by, [("ClientHello  ", GREY), ("SNI: ", WHITE), ("example.com", RED)], a0 * fade, f=font("mono", 24))
    # 阶段 2：分片从中继飞出 (循环)
    pieces = ["ex", "am", "pl", "e.", "co", "m"]
    frag_start = 3.4
    cycle = 5.2
    alert = 0.0
    if t > frag_start:
        tt = (t - frag_start) % cycle
        for i, pc in enumerate(pieces):
            st = i * 0.22
            pr = ease_in_out(clamp((tt - st) / 2.2))
            if tt < st:
                continue
            px = lerp(rx + rw + 50, sx - 50, pr)
            pa = min(1, (tt - st) * 6, max(0, (st + 2.2 + 0.5 - tt) * 4))
            if pa <= 0:
                continue
            near = 1 - clamp(abs(px - ex) / 200)
            alert = max(alert, near * 0.5)
            yoff = math.sin((pr * 3 + i) * 1.5) * 10
            draw_pill(d, px, by + yoff, [("rec ", GREY), (pc, CYAN)], pa * a0, f=font("mono", 22), h=44, pad=12, outline=CYAN)
        # 服务器端重组
        ra2 = clamp((tt - 2.9) / 0.5) * clamp((cycle - tt) / 0.4)
        if ra2 > 0:
            paste_glow(layer, (sx + sw / 2, by), 160, GREEN, 0.5 * ra2)
            d.text((sx + sw / 2 - 16, by - 150), "重组 → 握手完成", font=font("bold", 28), fill=with_a(GREEN, ra2), anchor="mm")
            draw_check(d, sx + sw / 2 + 112, by - 150, 22, with_a(GREEN, ra2), width=5)
    # 眼睛：疑惑
    eye_a = ease_out(seg(t, 1.0, 0.8))
    draw_eye(layer, d, ex, ey, eye_a, alert)
    d.text((ex, ey - 90), "中间设备", font=font("reg", 26), fill=with_a(GREY, eye_a), anchor="mm")
    qa = ease_out(seg(t, 5.2, 0.6))
    if qa > 0:
        bob = math.sin(t * 3) * 4
        d.text((ex + 110, ey - 40 + bob), "?", font=font("bold", 70), fill=with_a(AMBER, qa), anchor="mm")
        d.text((ex, ey + 95), "单个 record 里没有完整域名", font=font("reg", 24), fill=with_a(AMBER, qa), anchor="mm")
    # 底部要点
    pts = [("本机自签根证书，仅限本机", "仅当前用户 · 仅信任本机 nginx · 出站正常 TLS", AMBER),
           ("两条路线按站点自动选择", "nginx 改写 SNI，或回环中继分片、不解密", CYAN),
           ("只动握手阶段", "后续每个字节原样转发，视频流不受影响", VIOLET)]
    for i, (k, v, c) in enumerate(pts):
        ia = ease_out(seg(t, 8.0 + i * 0.3, 0.8))
        if ia <= 0:
            continue
        x = 160 + i * 540
        yb = 800 + (1 - ia) * 30
        rrect(d, (x, yb, x + 500, yb + 130), 18, fill=with_a(PANEL, ia * 0.95), outline=with_a(c, ia * 0.6), width=2)
        d.rectangle((x, yb + 18, x + 6, yb + 112), fill=with_a(c, ia))
        d.text((x + 30, yb + 42), k, font=font("bold", 30), fill=with_a(WHITE, ia), anchor="lm")
        d.text((x + 30, yb + 90), v, font=font("reg", 21), fill=with_a(GREY, ia), anchor="lm")


def scene_code(layer, d, t, dur):
    a0 = ease_out(seg(t, 0.0, 0.7))
    draw_heading(d, "核心代码：Direct/FragmentingStream.cs", 110, a0, 50, slide=(1 - a0) * -30)
    total_chars = sum(len(l) for l in CODE_LINES)
    type_t = seg(t, 0.4, 6.0)
    chars = int(ease_in_out(type_t) * total_chars) if type_t < 1 else total_chars
    f = font("mono", 21)
    hl = ease_out(seg(t, 6.8, 0.6))
    cursor_on = (int(t * 2.5) % 2 == 0) or type_t < 1
    ja = ease_out_back(seg(t, 9.2, 0.8))
    ja = ja if ja > 0.001 else 0.0
    draw_code_window(d, layer, 90, 150, 1340, 780, a0 * (1 - 0.6 * ja), "FragmentingStream.cs — WriteAsync", CODE_TOKENS, chars, f, 29, highlight_lines=range(9, 24), hl_alpha=hl, cursor_on=cursor_on and ja == 0)
    # 右侧说明卡
    notes = [("① 定位 SNI", "解析 ClientHello 扩展，找到 server_name 的偏移与长度", CYAN, 1.2),
             ("② 按字节切分", "FragmentBytes 默认 8，域名一定会被从中间切开", VIOLET, 3.0),
             ("③ 重排 record 边界", "每片补一个 5 字节 TLS 头，握手内容一字节不改", GREEN, 5.0),
             ("④ 可调间隔", "FragmentDelayMs 控制片与片之间的发送间隔", AMBER, 6.8)]
    for i, (k, v, c, st) in enumerate(notes):
        ia = ease_out(seg(t, st, 0.7)) * (1 - 0.6 * ja)
        if ia <= 0:
            continue
        x = 1470
        yb = 190 + i * 150 + (1 - ia) * 20
        rrect(d, (x, yb, x + 360, yb + 128), 16, fill=with_a(PANEL, ia * 0.95), outline=with_a(c, ia * 0.6), width=2)
        d.text((x + 22, yb + 36), k, font=font("bold", 28), fill=with_a(c, ia), anchor="lm")
        # 简单换行
        words = v
        f2 = font("reg", 21)
        lines, cur = [], ""
        for ch in words:
            if text_w(d, cur + ch, f2) > 316:
                lines.append(cur)
                cur = ch
            else:
                cur += ch
        lines.append(cur)
        for j, ln in enumerate(lines[:2]):
            d.text((x + 22, yb + 72 + j * 28), ln, font=f2, fill=with_a(GREY, ia), anchor="lm")
    # JSON 配置弹层
    if ja > 0:
        jw, jh = 1240, 380
        cx, cy = W / 2, H / 2 + 20
        sc = 0.85 + 0.15 * ja
        hw, hh = jw / 2 * sc, jh / 2 * sc
        paste_glow(layer, (cx, cy), 300, CYAN, 0.25 * ja)
        rrect(d, (cx - hw, cy - hh, cx + hw, cy + hh), 18, fill=with_a((13, 17, 30), ja), outline=with_a(CYAN, ja * 0.8), width=2)
        if ja > 0.5:
            fa = (ja - 0.5) / 0.5
            d.text((cx, cy - hh + 30), "direct-config.json — 所有参数集中在一份配置里", font=font("reg", 24), fill=with_a(GREY, fa), anchor="mm")
            fj = font("mono", 25)
            y = cy - hh + 76
            for li, (ln, col) in enumerate(JSON_LINES):
                la = ease_out(seg(t, 9.7 + li * 0.08, 0.3))
                x = cx - hw + 40
                for s, c in json_tokens(ln):
                    d.text((x, y), s, font=fj, fill=with_a(c, la * fa), anchor="la")
                    x += text_w(d, s, fj)
                y += 36


def draw_app_mock(layer, d, x, y, alpha, t):
    w, h = 560, 640
    rrect(d, (x, y, x + w, y + h), 16, fill=with_a((245, 247, 252), alpha), outline=with_a((200, 206, 220), alpha), width=2)
    # 标题栏
    rrect(d, (x, y, x + w, y + 44), 16, fill=with_a((232, 236, 246), alpha))
    d.rectangle((x, y + 28, x + w, y + 44), fill=with_a((232, 236, 246), alpha))
    d.text((x + 20, y + 22), "雾帆 MistSail", font=font("bold", 18), fill=with_a((30, 36, 54), alpha), anchor="lm")
    tc = with_a((90, 100, 125), alpha)
    d.line((x + w - 104, y + 22, x + w - 92, y + 22), fill=tc, width=2)
    d.rectangle((x + w - 72, y + 16, x + w - 60, y + 28), outline=tc, width=2)
    d.line((x + w - 40, y + 16, x + w - 28, y + 28), fill=tc, width=2)
    d.line((x + w - 40, y + 28, x + w - 28, y + 16), fill=tc, width=2)
    d.text((x + 30, y + 80), "雾帆 MistSail", font=font("bold", 26), fill=with_a((30, 36, 54), alpha), anchor="lm")
    d.text((x + 30, y + 112), "让握手藏进雾里 · 本机握手处理", font=font("reg", 16), fill=with_a((110, 120, 145), alpha), anchor="lm")
    # 电源按钮
    cx, cy = x + w / 2, y + 260
    on = seg(t, 1.6, 0.5)
    col = lerp_c((120, 132, 160), (34, 197, 94), ease_out(on))
    pulse = 0.5 + 0.5 * math.sin(t * 3.0)
    paste_glow(layer, (cx, cy), 120, (34, 197, 94), alpha * on * (0.5 + 0.3 * pulse))
    pressed = 1 - 0.08 * math.sin(math.pi * seg(t, 1.3, 0.4)) if 1.3 <= t <= 1.7 else 1
    r = 86 * pressed
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=with_a(col, alpha))
    # power icon
    r2 = 34
    d.arc((cx - r2, cy - r2, cx + r2, cy + r2), start=-55, end=235, fill=with_a(WHITE, alpha), width=8)
    d.line((cx, cy - r2 - 6, cx, cy - 4), fill=with_a(col, alpha), width=14)
    d.line((cx, cy - r2 - 6, cx, cy - 4), fill=with_a(WHITE, alpha), width=8)
    status = "已连接" if on >= 1 else ("连接中…" if on > 0 else "未连接")
    d.text((cx, cy + 130), status, font=font("bold", 30), fill=with_a((30, 36, 54), alpha), anchor="mm")
    d.text((cx, cy + 168), "本机 nginx 与分片中继运行中 · 8 字节 / 8 ms" if on >= 1 else "点击开始", font=font("reg", 16), fill=with_a((110, 120, 145), alpha), anchor="mm")
    # 连通性卡片
    cy2 = y + 470
    rrect(d, (x + 24, cy2, x + w - 24, cy2 + 140), 12, fill=with_a((255, 255, 255), alpha), outline=with_a((215, 220, 232), alpha), width=1)
    d.text((x + 40, cy2 + 26), "连通性", font=font("bold", 18), fill=with_a((30, 36, 54), alpha), anchor="lm")
    for i, lb in enumerate(["重新检测", "线路探测", "复制诊断信息"]):
        bx = x + w - 40 - (3 - i) * 108
        rrect(d, (bx, cy2 + 12, bx + 100, cy2 + 40), 8, fill=with_a((233, 239, 252), alpha))
        d.text((bx + 50, cy2 + 26), lb, font=font("reg", 14), fill=with_a((40, 90, 200), alpha), anchor="mm")
    rows = [("站点 A", "ServerHello 180 ms"), ("站点 B", "ServerHello 96 ms"), ("DoH 解析", "Cloudflare / Quad9 OK")]
    for i, (k, v) in enumerate(rows):
        ra = ease_out(seg(t, 2.3 + i * 0.3, 0.5))
        yy = cy2 + 64 + i * 26
        d.ellipse((x + 44, yy - 7, x + 58, yy + 7), fill=with_a((34, 197, 94) if ra > 0.5 else (200, 206, 220), alpha))
        d.text((x + 70, yy), k, font=font("reg", 16), fill=with_a((30, 36, 54), alpha), anchor="lm")
        d.text((x + w - 44, yy), v if ra > 0.5 else "检测中…", font=font("mono", 15), fill=with_a((110, 120, 145), alpha), anchor="rm")
    d.text((x + w / 2 - 8, y + h - 18), "更多功能", font=font("reg", 15), fill=with_a((110, 120, 145), alpha), anchor="mm")
    mx = x + w / 2 + 34
    d.line([(mx - 6, y + h - 21), (mx, y + h - 15), (mx + 6, y + h - 21)], fill=with_a((110, 120, 145), alpha), width=2)


def scene_ui(layer, d, t, dur):
    a0 = ease_out(seg(t, 0.0, 0.7))
    draw_heading(d, "一个按钮，剩下的交给它", 110, a0, 56, slide=(1 - a0) * -30)
    ma = ease_out(seg(t, 0.3, 0.9))
    draw_app_mock(layer, d, 200 + (1 - ma) * -60, 200, ma, t)
    feats = [("一键连接", "主界面只保留一个电源按钮，旧功能收进「更多功能」", CYAN),
             ("无代理服务器", "没有境外节点、没有 VPN，TCP 连接直接从本机发往目标", GREEN),
             ("根证书透明可查", "自签证书仅信任本机 nginx，可随时在证书管理器里删除", VIOLET),
             ("开源可审计", "C# 源码全部公开，核心分片逻辑一百多行", AMBER)]
    for i, (k, v, c) in enumerate(feats):
        ia = ease_out(seg(t, 1.0 + i * 0.35, 0.8))
        if ia <= 0:
            continue
        x = 880 + (1 - ia) * 60
        yb = 230 + i * 165
        rrect(d, (x, yb, x + 860, yb + 130), 18, fill=with_a(PANEL, ia * 0.95), outline=with_a(c, ia * 0.6), width=2)
        d.ellipse((x + 28, yb + 40, x + 78, yb + 90), fill=with_a(c, ia * 0.18), outline=with_a(c, ia), width=3)
        draw_check(d, x + 53, yb + 65, 22, with_a(c, ia), width=5)
        d.text((x + 110, yb + 44), k, font=font("bold", 32), fill=with_a(WHITE, ia), anchor="lm")
        d.text((x + 110, yb + 92), v, font=font("reg", 24), fill=with_a(GREY, ia), anchor="lm")


def scene_outro(layer, d, t, dur):
    a0 = ease_out(seg(t, 0.0, 1.0))
    cx, cy = W / 2, H / 2 - 120
    paste_glow(layer, (cx, cy), 260, CYAN, 0.3 * a0)
    paste_glow(layer, (cx + 200, cy + 100), 220, VIOLET, 0.2 * a0)
    r = 70
    pts = [(cx - r * 0.9, cy - 20), (cx + r * 0.75, cy - 20 - r * 0.9), (cx + r * 0.75, cy - 20 + r * 0.9)]
    d.polygon(pts, fill=with_a(CYAN, a0 * 0.22), outline=with_a(CYAN, a0))
    d.line(pts + [pts[0]], fill=with_a(CYAN, a0), width=8)
    d.text((cx - 120, cy + 105), "雾帆", font=font("bold", 104), fill=with_a(WHITE, a0), anchor="mm")
    d.text((cx + 125, cy + 112), "MistSail", font=font("light", 78), fill=with_a(CYAN, a0), anchor="mm")
    sa = ease_out(seg(t, 0.6, 0.8))
    d.text((cx, cy + 190), "基于开源项目 Sheas Cealer 1.1.4 二次开发 · WPF / .NET 8 · 开源", font=font("light", 30), fill=with_a(GREY, sa), anchor="mm")
    da = ease_out(seg(t, 1.4, 0.9))
    if da > 0:
        yb = H / 2 + 190
        rrect(d, (W / 2 - 700, yb - 70, W / 2 + 700, yb + 70), 22, fill=with_a((30, 26, 20), da * 0.9), outline=with_a(AMBER, da * 0.8), width=2)
        d.text((W / 2, yb - 22), "本项目及相关资源仅供抵御网络非法监听与开展网络安全研究使用", font=font("bold", 30), fill=with_a((254, 240, 200), da), anchor="mm")
        d.text((W / 2, yb + 26), "请遵守所在地法律法规 · 不保证任何站点可用 · 使用前请阅读注意事项与用户协议", font=font("reg", 24), fill=with_a(AMBER, da), anchor="mm")
    ga = ease_out(seg(t, 2.4, 0.8))
    d.text((W / 2, H / 2 + 318), "开源  ·  本地改版  ·  2026", font=font("reg", 30), fill=with_a(CYAN, ga), anchor="mm")
    ca = ease_out(seg(t, CREDIT_AT, 0.8))
    if ca > 0:
        paste_glow(layer, (W / 2, H / 2 + 372), 140, VIOLET, 0.25 * ca)
        d.text((W / 2, H / 2 + 372 + (1 - ca) * 16), "本片由 Claude Fable 5.1 制作  ·  配音：Edge 神经网络语音", font=font("bold", 30), fill=with_a((221, 214, 254), ca), anchor="mm")
    # 结尾淡出
    fade = 1 - seg(t, dur - 1.2, 1.2)
    if fade < 1:
        d.rectangle((0, 0, W, H), fill=(0, 0, 0, int(255 * (1 - fade))))


SCENES = [
    (0.0, 6.0, scene_intro),
    (6.0, 16.0, scene_problem),
    (16.0, 24.0, scene_block),
    (24.0, 38.0, scene_solution),
    (38.0, 50.0, scene_code),
    (50.0, 57.0, scene_ui),
    (57.0, 63.0, scene_outro),
]
XFADE = 0.6

SUBS = [
    (0.8, 5.6, "雾帆 MistSail：一款基于 WPF / .NET 8 的开源 SNI 处理工具"),
    (6.5, 11.0, "HTTPS 加密了网页内容，却有一处例外：握手阶段的 SNI"),
    (11.0, 15.8, "SNI 以明文写着你要访问的域名，链路上任何设备都能看见"),
    (16.5, 20.5, "于是中间设备可以据此嗅探、劫持，甚至直接重置你的连接"),
    (20.5, 23.8, "内容是加密的，但「你在访问谁」却是公开的"),
    (24.5, 29.0, "雾帆的思路：不加代理，在本机接管握手"),
    (29.0, 34.0, "hosts 把域名指向本机，由本机 nginx 改写 SNI 后连向真实服务器"),
    (34.0, 37.8, "Cloudflare 类站点则由回环中继把 ClientHello 切片透传"),
    (38.5, 43.5, "核心代码只有一百多行：定位 SNI 扩展，按字节切分，重排记录边界"),
    (43.5, 47.5, "根证书本机自签，只装当前用户、只信任本机 nginx；分片路线不解密"),
    (47.5, 49.8, "所有参数都在一份 JSON 里，分片字节、间隔、模式随时可调"),
    (50.5, 54.0, "主界面只有一个电源按钮，连通性检测与诊断信息一键复制"),
    (54.0, 56.8, "无代理服务器，不改路由，流量不经任何第三方"),
    (57.5, 61.6, "开源可审计 · 仅供抵御网络非法监听与网络安全研究 · 请遵守当地法律法规"),
]


CREDIT_AT = 3.4
_tl_path = os.path.join(OUT_DIR, "timeline.json")
if os.path.exists(_tl_path):
    import json as _json
    with open(_tl_path, encoding="utf-8") as _fh:
        _tl = _json.load(_fh)
    DURATION = float(_tl["duration"])
    TOTAL = int(DURATION * FPS)
    SCENES = [(float(a), float(b), fn) for (a, b), (_, _, fn) in zip(_tl["scenes"], SCENES)]
    SUBS = [(x["start"], x["end"], x["text"]) for x in _tl["subs"]]
    for x in _tl["subs"]:
        if "Fable" in x["text"]:
            CREDIT_AT = max(0.3, x["start"] - SCENES[-1][0] - 0.2)


def draw_subtitle(layer, d, t):
    f = font("bold", 40)
    for s, e, text in SUBS:
        if s - 0.3 <= t <= e + 0.3:
            a = min(1, (t - (s - 0.3)) / 0.35, ((e + 0.3) - t) / 0.3)
            a = clamp(a)
            slide = (1 - ease_out(a)) * 14
            tw = text_w(d, text, f)
            y = H - 96 + slide
            rrect(d, (W / 2 - tw / 2 - 34, y - 38, W / 2 + tw / 2 + 34, y + 38), 20, fill=(8, 10, 18, int(190 * a)))
            d.text((W / 2 + 2, y + 2), text, font=f, fill=(0, 0, 0, int(160 * a)), anchor="mm")
            d.text((W / 2, y), text, font=f, fill=with_a(WHITE, a), anchor="mm")


def draw_chrome(d, t):
    """左上角常驻标识 + 右上进度条"""
    a = ease_out(seg(t, 0.5, 1.0)) * (1 - seg(t, DURATION - 1.2, 1.2))
    if a <= 0:
        return
    draw_tri(d, 72, 56, 14, fill=with_a(CYAN, a * 0.3), outline=with_a(CYAN, a), width=3)
    d.text((104, 56), "雾帆 MistSail  ·  项目介绍", font=font("reg", 24), fill=with_a(GREY, a), anchor="lm")
    # 进度条
    p = t / DURATION
    d.rectangle((W - 360, 52, W - 60, 56), fill=with_a((40, 50, 78), a))
    d.rectangle((W - 360, 52, W - 360 + 300 * p, 56), fill=with_a(CYAN, a))


_BG = None


def render_frame(i):
    global _BG
    if _BG is None:
        _BG = make_background()
    t = i / FPS
    frame = _BG.copy()
    for si, (s, e, fn) in enumerate(SCENES):
        if not (s - XFADE <= t < e + XFADE):
            continue
        if t < s or t >= e:
            continue
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        fn(layer, d, t - s, e - s)
        alpha = 1.0
        if si > 0:
            alpha *= ease_in_out(seg(t, s, XFADE))
        if si < len(SCENES) - 1:
            alpha *= 1 - ease_in_out(seg(t, e - XFADE, XFADE))
        if alpha < 1:
            arr = np.asarray(layer)
            a = (arr[..., 3].astype(np.float32) * alpha).astype(np.uint8)
            layer = Image.fromarray(np.dstack([arr[..., :3], a]), "RGBA")
        frame.alpha_composite(layer)
    top = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(top)
    draw_subtitle(top, d, t)
    draw_chrome(d, t)
    frame.alpha_composite(top)
    return frame.convert("RGB").tobytes()


def encode_segment(args):
    idx, start, end, ffmpeg = args
    out = os.path.join(OUT_DIR, f"seg_{idx:02d}.mp4")
    cmd = [ffmpeg, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-",
           "-c:v", "libx264", "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p", "-g", str(FPS * 2), out]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for i in range(start, end):
        p.stdin.write(render_frame(i))
    p.stdin.close()
    p.wait()
    return out


def main():
    import imageio_ffmpeg
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    if len(sys.argv) > 1 and sys.argv[1] == "preview":
        # 导出若干关键帧 PNG 供检查
        for sec in [2.5, 5.0, 9.5, 13.0, 19.0, 22.5, 27.0, 31.5, 36.0, 41.0, 46.0, 49.5, 53.5, 56.0, 60.0]:
            data = render_frame(int(sec * FPS))
            Image.frombytes("RGB", (W, H), data).save(os.path.join(OUT_DIR, f"preview_{sec:05.1f}.png"))
        print("preview done")
        return
    workers = max(2, min(10, os.cpu_count() or 4))
    n = workers * 2
    bounds = [(k * TOTAL // n, (k + 1) * TOTAL // n) for k in range(n)]
    jobs = [(k, s, e, ffmpeg) for k, (s, e) in enumerate(bounds)]
    with Pool(workers) as pool:
        segs = pool.map(encode_segment, jobs)
    lst = os.path.join(OUT_DIR, "segs.txt")
    with open(lst, "w", encoding="utf-8") as fh:
        for sgm in segs:
            fh.write("file '" + sgm.replace("\\", "/") + "'\n")
    silent = os.path.join(OUT_DIR, "video_silent.mp4")
    subprocess.check_call([ffmpeg, "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", lst, "-c", "copy", silent])
    print("video done:", silent)


if __name__ == "__main__":
    main()
