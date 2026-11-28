# Traces the one-colour glyphs in source/icons-mono.jpg into SVGs with one fill and a transparent
# background: mono-smoke.svg, mono-orbit.svg. The image's top row has them as black shapes on two white
# panels, Smoke on the left and Orbit on the right (its bottom row, the same inverted, is not used).
# render-icon.ps1 recolours their #000000 fill for the tray, the toolbar and the title bar.
# Run it again only if the source image changes.
#
# How: inside each panel the glyph coverage is (white - brightness) / (white - black), lightly blurred
# to take out JPEG noise; its 0.5 contour is found by marching squares, smoothed along its length,
# thinned (Ramer-Douglas-Peucker) and written as cubic Bezier curves (Catmull-Rom), with sharp corners
# kept sharp. Loops are filled even-odd, so holes stay holes. Each glyph is framed square and tight.
#
# Needs Python 3 with Pillow, NumPy and SciPy.
# Usage: python scripts\icon\trace-mono.py
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

HERE = Path(__file__).resolve().parent
SOURCE = HERE / 'source' / 'icons-mono.jpg'
OUT = {'smoke': HERE / 'mono-smoke.svg', 'orbit': HERE / 'mono-orbit.svg'}
MIN_AREA = 40       # px^2: loops smaller than this are JPEG specks
SMOOTH = 1.2        # points: smoothing along a contour
TOLERANCE = 0.45    # px: how far the thinned outline may stray from the traced one
CORNER = 55         # degrees: a sharper turn than this stays a corner


def panel(lum, cols):
    """The inside of the white panel in the top row of these columns, a little inset."""
    top = lum[:lum.shape[0] // 2, cols]
    ys, xs = np.nonzero(top > 200)
    inset = 14
    # The panel's lowest rows are a grey strip; stop above it.
    bottom = ys.max()
    while np.median(top[bottom, xs.min() + inset:xs.max() - inset]) < 235:
        bottom -= 1
    return lum[ys.min() + inset:bottom - inset, cols][:, xs.min() + inset:xs.max() - inset]


def coverage(lum):
    white, black = np.percentile(lum, 95), np.percentile(lum, 3)
    return ndi.gaussian_filter(np.clip((white - lum) / (white - black), 0, 1), 1.0)


def contours(f, level=0.5):
    """Closed contour loops of f at level, by marching squares, as arrays of (x, y)."""
    h, w = f.shape
    f = np.pad(f, 1)  # so every loop closes inside the image
    h, w = h + 2, w + 2
    above = f > level

    def cross(p, q):  # the point where the level crosses the grid edge p-q, keyed by the edge
        (y0, x0), (y1, x1) = p, q
        a, b = f[y0, x0], f[y1, x1]
        t = (level - a) / (b - a)
        return (min(p, q), max(p, q)), (x0 + t * (x1 - x0), y0 + t * (y1 - y0))

    nxt, pos = {}, {}
    for y in range(h - 1):
        row = above[y]
        rown = above[y + 1]
        for x in range(w - 1):
            c = (row[x] << 3) | (row[x + 1] << 2) | (rown[x + 1] << 1) | rown[x]
            if c == 0 or c == 15:
                continue
            tl, tr, br, bl = (y, x), (y, x + 1), (y + 1, x + 1), (y + 1, x)
            top, right, bottom, left = (tl, tr), (tr, br), (bl, br), (tl, bl)
            # Segments run with the inside on the left; the saddles split by the centre value.
            centre = f[y:y + 2, x:x + 2].mean() > level
            table = {1: [(left, bottom)], 2: [(bottom, right)], 3: [(left, right)], 4: [(right, top)],
                     5: [(left, top), (right, bottom)] if centre else [(left, bottom), (right, top)],
                     6: [(bottom, top)], 7: [(left, top)], 8: [(top, left)], 9: [(top, bottom)],
                     10: [(top, right), (bottom, left)] if centre else [(top, left), (bottom, right)],
                     11: [(top, right)], 12: [(right, left)], 13: [(right, bottom)], 14: [(bottom, left)]}
            for a, b in table[c]:
                ka, pa = cross(*a)
                kb, pb = cross(*b)
                nxt[ka] = kb
                pos[ka], pos[kb] = pa, pb
    loops, seen = [], set()
    for start in nxt:
        if start in seen:
            continue
        loop, k = [], start
        while k in nxt and k not in seen:
            seen.add(k)
            loop.append(pos[k])
            k = nxt[k]
        if len(loop) > 3:
            loops.append(np.array(loop) - 1)  # undo the padding
    return loops


def area(p):
    x, y = p[:, 0], p[:, 1]
    return 0.5 * (np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def rdp(p, tol):
    """Ramer-Douglas-Peucker on an open polyline; returns the kept indices."""
    keep = np.zeros(len(p), bool)
    keep[0] = keep[-1] = True
    stack = [(0, len(p) - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        a, b = p[i], p[j]
        ab = b - a
        n = np.hypot(*ab)
        seg = p[i + 1:j] - a
        d = np.abs(ab[0] * seg[:, 1] - ab[1] * seg[:, 0]) / n if n > 1e-9 else np.hypot(seg[:, 0], seg[:, 1])
        k = int(np.argmax(d))
        if d[k] > tol:
            keep[i + 1 + k] = True
            stack += [(i, i + 1 + k), (i + 1 + k, j)]
    return np.nonzero(keep)[0]


def to_path(p, ox, oy, scale):
    """A closed loop as an SVG path: Catmull-Rom curves through the thinned points, corners kept."""
    # Smooth along the loop, but not across corners (find corners first on the raw loop).
    sm = np.stack([ndi.gaussian_filter1d(p[:, i], SMOOTH, mode='wrap') for i in range(2)], -1)
    # Thin: start the open polyline at the point furthest from the centroid, so the seam is a real point.
    s = int(np.argmax(np.hypot(*(sm - sm.mean(0)).T)))
    ring = np.roll(sm, -s, axis=0)
    ring = np.vstack([ring, ring[:1]])
    pts = ring[rdp(ring, TOLERANCE)][:-1]
    n = len(pts)
    if n < 3:
        return ''
    # Turning angle at each point decides corner (straight tangents) or smooth (Catmull-Rom tangents).
    prev, nxt = np.roll(pts, 1, 0), np.roll(pts, -1, 0)
    v1, v2 = pts - prev, nxt - pts
    ang = np.degrees(np.abs(np.arctan2(v1[:, 0] * v2[:, 1] - v1[:, 1] * v2[:, 0], (v1 * v2).sum(1))))
    corner = ang > CORNER
    tang = (nxt - prev) / 6
    tang[corner] = 0
    q = (pts - [ox, oy]) * scale
    t = tang * scale

    def fmt(v):
        return f'{v[0]:.1f} {v[1]:.1f}'
    d = [f'M{fmt(q[0])}']
    for i in range(n):
        j = (i + 1) % n
        c1, c2 = q[i] + t[i], q[j] - t[j]
        if corner[i] and corner[j]:
            d.append(f'L{fmt(q[j])}')
        else:
            d.append(f'C{fmt(c1)} {fmt(c2)} {fmt(q[j])}')
    return ''.join(d) + 'Z'


lum_all = np.asarray(Image.open(SOURCE).convert('L')).astype(np.float64)
half = lum_all.shape[1] // 2
for name, cols in (('smoke', slice(0, half)), ('orbit', slice(half, None))):
    f = coverage(panel(lum_all, cols))
    # The glyph is several separate shapes (globe, rocket, cloud or ring); specks go by size.
    lab, n = ndi.label(f > 0.5)
    sizes = np.bincount(lab.ravel())
    f = np.where(np.isin(lab, np.nonzero(sizes >= MIN_AREA)[0][1:]) | (ndi.binary_dilation(lab > 0, iterations=2) & ~(lab > 0)), f, 0)
    loops = [p for p in contours(f) if abs(area(p)) >= MIN_AREA]
    ys, xs = np.nonzero(f > 0.5)
    side = max(xs.max() - xs.min(), ys.max() - ys.min()) + 6
    ox, oy = (xs.min() + xs.max()) / 2 - side / 2, (ys.min() + ys.max()) / 2 - side / 2
    scale = 1024 / side
    paths = ''.join(to_path(p, ox, oy, scale) for p in loops)
    svg = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">\n'
           f'  <!-- One-colour {name.capitalize()} glyph, traced from source/icons-mono.jpg by trace-mono.py.\n'
           '       render-icon.ps1 swaps the #000000 fill for the tray, toolbar and title-bar colours. -->\n'
           f'  <path fill="#000000" fill-rule="evenodd" d="{paths}"/>\n</svg>\n')
    OUT[name].write_text(svg, encoding='utf-8')
    print(f'{name}: {len(loops)} loops, {len(paths) // 1024} KB -> {OUT[name].name}')
