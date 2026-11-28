# Cuts the two app icon variants out of their artwork renders (the icon on a flat dark-grey backdrop)
# into transparent 1024 x 1024 PNG masters, which render-icon.ps1 and the installer art are made from:
#   Orbit  source/icon-master.jpg        -> icon-master.png        (the default app icon)
#   Smoke  source/icon-smoke-master.jpg  -> icon-smoke-master.png  (the same icon with a smoke trail)
# Run it again only if a source image changes.
#
# How: the backdrop is a smooth dark gradient with the tile's shadow on it, so it is modelled from the
# image border. Orbit is removed from the outside in: everything connected to the border that is not
# brighter or more colourful than the backdrop (the shadow is only darker) is background. Along the edge
# the alpha is solved per pixel from the nearest solid icon colour and the nearest backdrop colour, and
# the colour is un-mixed from the backdrop, so the edge keeps its anti-aliasing without a dark fringe.
# The two renders line up pixel for pixel, so Smoke takes Orbit's outline of the tile and rocket; its
# smoke outside the tile is semi-transparent, so there the alpha comes from colour-difference matting
# against the backdrop model (how far the pixel has moved from the backdrop towards the smoke's own
# colour), and the colour is un-mixed the same way, leaving soft edges with no grey halo.
# Both squares are centred on the tile, wide enough for the rocket that breaks out of it, and framed
# the same, so the two variants swap without the tile moving.
#
# Needs Python 3 with Pillow, NumPy and SciPy.
# Usage: python scripts\icon\cutout-master.py
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

HERE = Path(__file__).resolve().parent
ORBIT = (HERE / 'source' / 'icon-master.jpg', HERE / 'icon-master.png')
SMOKE = (HERE / 'source' / 'icon-smoke-master.jpg', HERE / 'icon-smoke-master.png')
SIZE = 1024
BAND = 3  # px either side of the edge where alpha is solved


def load(path):
    return np.asarray(Image.open(path).convert('RGB')).astype(np.float64)


def backdrop(img):
    """A quadratic surface per channel fitted to a strip along the border."""
    h, w, _ = img.shape
    yy, xx = np.mgrid[0:h, 0:w]
    border = np.zeros((h, w), bool)
    border[:60, :] = border[-60:, :] = border[:, :60] = border[:, -60:] = True
    u, v = xx / w, yy / h
    terms = np.stack([np.ones_like(u), u, v, u * u, u * v, v * v], -1)
    A = terms[border]
    return np.stack([terms @ np.linalg.lstsq(A, img[..., c][border], rcond=None)[0] for c in range(3)], -1)


def outline(img, bg_model):
    """The icon as a hard mask: what is brighter or more colourful than the backdrop, filled."""
    d = img - bg_model
    brighter = np.clip(d, 0, None).sum(-1)
    chroma = np.linalg.norm((img - img.mean(-1, keepdims=True)) - (bg_model - bg_model.mean(-1, keepdims=True)), axis=-1)
    low = (brighter + 1.5 * chroma) < 60
    labels, _ = ndi.label(low)
    edge = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
    fg = ~np.isin(labels, edge[edge > 0])
    fl, _ = ndi.label(fg)
    return fl == (np.argmax(np.bincount(fl.ravel())[1:]) + 1)  # the icon only, no stray specks


def hard_cut(img, fg):
    """Alpha and colour for a hard-edged icon, solved across a thin band along the outline."""
    inner = ndi.binary_erosion(fg, iterations=BAND + 1)
    outer = ~ndi.binary_dilation(fg, iterations=BAND + 1)  # (not an erosion of ~fg: that eats the image border)
    _, (iy, ix) = ndi.distance_transform_edt(~inner, return_indices=True)
    _, (oy, ox) = ndi.distance_transform_edt(~outer, return_indices=True)
    F, B = img[iy, ix], img[oy, ox]
    alpha = fg.astype(np.float64)
    band = ~inner & ~outer
    fb = F - B
    den = (fb * fb).sum(-1)
    solved = np.clip(((img - B) * fb).sum(-1) / np.maximum(den, 1e-6), 0, 1)
    ok = band & (den > 400)  # enough contrast to solve; elsewhere keep the hard edge, softened
    alpha[ok] = solved[ok]
    soft = ndi.gaussian_filter(fg.astype(np.float64), 0.7)
    alpha[band & ~ok] = soft[band & ~ok]
    colour = img.copy()
    a = alpha[..., None]
    unmixed = np.clip((img - (1 - a) * B) / np.maximum(a, 1e-6), 0, 255)
    wgt = np.clip((a - 0.15) / 0.5, 0, 1)
    colour[band] = (wgt * unmixed + (1 - wgt) * F)[band]
    return alpha, colour, inner


def frame(alpha):
    """The square crop: centred on the tile, wide enough for everything drawn."""
    h, w = alpha.shape
    ys, xs = np.nonzero(alpha > 0.5)
    left, top, bottom = xs.min(), ys.min(), ys.max()
    row = int(top + 0.85 * (bottom - top))  # the tile's right edge, read low down, below the rocket
    right = np.nonzero(alpha[row] > 0.5)[0].max()
    cx, cy = (left + right) / 2, (top + bottom) / 2
    any_ys, any_xs = np.nonzero(alpha > 0.01)
    half = max(np.abs(any_xs - cx).max(), np.abs(any_ys - cy).max()) + 6
    side = int(round(2 * half))
    return int(round(cx - half)), int(round(cy - half)), side, (right - left) / side


def save(colour, alpha, box, path):
    x0, y0, side, _ = box
    h, w = alpha.shape
    rgba = np.dstack([colour, alpha * 255])
    pad = max(0, -x0, -y0, x0 + side - w, y0 + side - h)
    if pad:
        rgba = np.pad(rgba, ((pad, pad), (pad, pad), (0, 0)))
    crop = rgba[y0 + pad:y0 + pad + side, x0 + pad:x0 + pad + side]
    # Resize premultiplied so the transparent surround cannot bleed into the edge.
    premul = crop.copy()
    premul[..., :3] *= premul[..., 3:4] / 255
    chans = [Image.fromarray(premul[..., c].astype(np.float32), 'F').resize((SIZE, SIZE), Image.LANCZOS) for c in range(4)]
    out = np.stack([np.asarray(ch) for ch in chans], -1)
    out[..., 3] = np.clip(out[..., 3], 0, 255)
    out[..., :3] = np.where(out[..., 3:4] > 0.5, out[..., :3] * 255 / np.maximum(out[..., 3:4], 1e-6), 0)
    Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8), 'RGBA').save(path, optimize=True)


# ---- Orbit ----
img = load(ORBIT[0])
bg_model = backdrop(img)
fg = outline(img, bg_model)
alpha, colour, _ = hard_cut(img, fg)
box = frame(alpha)
save(colour, alpha, box, ORBIT[1])
print(f'Orbit: crop {box[2]}px at ({box[0]}, {box[1]}), tile fills {box[3]:.0%} -> {ORBIT[1].name}')

# ---- Smoke: Orbit's outline for the tile and rocket, matting for the smoke outside it ----
img = load(SMOKE[0])
bg_model = backdrop(img)
alpha_h, colour_h, inner = hard_cut(img, fg)
lum = img.mean(-1)
bg_lum = bg_model.mean(-1)
outside = ~ndi.binary_dilation(fg, iterations=BAND + 1)
# The smoke's own colour: its brightest parts, which hide the backdrop completely.
smoke_px = img[outside & (lum - bg_lum > 40)]
smoke_lum = np.percentile(smoke_px.mean(-1), 99.5)
smoke_rgb = smoke_px[smoke_px.mean(-1) >= np.percentile(smoke_px.mean(-1), 98)].mean(0)
# Alpha from how far a pixel has brightened from the backdrop towards the smoke (darker, i.e. shadow, is 0).
matte = np.clip((lum - bg_lum) / np.maximum(smoke_lum - bg_lum, 1), 0, 1)
matte = np.clip((matte - 0.04) / 0.96, 0, 1)  # drop JPEG noise on the backdrop
labels, _ = ndi.label(matte > 0)  # keep only smoke that touches the icon (no specks out on the backdrop)
touching = np.unique(labels[ndi.binary_dilation(fg, iterations=BAND + 2) & (labels > 0)])
matte[~np.isin(labels, touching)] = 0
alpha_s = np.where(inner, 1.0, np.maximum(alpha_h, np.where(fg, 0, matte)))
# Colour: un-mix the backdrop where the smoke is the alpha; near-clear wisps take the smoke's colour.
a = matte[..., None]
unmixed = np.clip(bg_model + (img - bg_model) / np.maximum(a, 1e-6), 0, 255)
wgt = np.clip((a - 0.05) / 0.3, 0, 1)
smoke_colour = wgt * unmixed + (1 - wgt) * smoke_rgb
use_smoke = (matte > alpha_h) & ~inner
colour_s = np.where(use_smoke[..., None], smoke_colour, colour_h)
box_s = frame(alpha_s)
if box_s[2] > box[2]:  # framed like Orbit unless the smoke reaches further than the rocket
    print(f'note: the smoke needs a {box_s[2]}px square; Orbit framing is {box[2]}px')
save(colour_s, alpha_s, box, SMOKE[1])
print(f'Smoke: framed as Orbit, smoke colour {smoke_rgb.round()} -> {SMOKE[1].name}')
