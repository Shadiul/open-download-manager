# Cuts the app icon out of its artwork render (source/icon-master.jpg: the icon on a flat dark-grey
# backdrop) into a transparent PNG master (icon-master.png, 1024 x 1024) that render-icon.ps1 and the
# installer art are made from. Run it again only if the source image changes.
#
# How: the backdrop is a smooth dark gradient with the tile's shadow on it, so it is modelled from the
# image border and removed from the outside in: everything connected to the border that is not brighter
# or more colourful than the backdrop (the shadow is only darker) is background. Along the edge the alpha
# is solved per pixel from the nearest solid icon colour and the nearest backdrop colour, and the colour
# is un-mixed from the backdrop, so the edge keeps its anti-aliasing without a dark fringe.
# The square is centred on the tile, wide enough for the rocket that breaks out of it.
#
# Needs Python 3 with Pillow, NumPy and SciPy.
# Usage: python scripts\icon\cutout-master.py
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

HERE = Path(__file__).resolve().parent
SOURCE = HERE / 'source' / 'icon-master.jpg'
MASTER = HERE / 'icon-master.png'
SIZE = 1024
BAND = 3  # px either side of the edge where alpha is solved

img = np.asarray(Image.open(SOURCE).convert('RGB')).astype(np.float64)
h, w, _ = img.shape
yy, xx = np.mgrid[0:h, 0:w]

# Backdrop model: a quadratic surface per channel fitted to a strip along the border.
border = np.zeros((h, w), bool)
border[:60, :] = border[-60:, :] = border[:, :60] = border[:, -60:] = True
u, v = xx / w, yy / h
terms = np.stack([np.ones_like(u), u, v, u * u, u * v, v * v], -1)
A = terms[border]
bg_model = np.stack([terms @ np.linalg.lstsq(A, img[..., c][border], rcond=None)[0] for c in range(3)], -1)

# How much a pixel stands out from the backdrop: brighter, or a different colour. Darker (the
# tile's shadow) does not count.
d = img - bg_model
brighter = np.clip(d, 0, None).sum(-1)
chroma = np.linalg.norm((img - img.mean(-1, keepdims=True)) - (bg_model - bg_model.mean(-1, keepdims=True)), axis=-1)
score = brighter + 1.5 * chroma

# Background = the low-score region connected to the border; the icon is everything else.
low = score < 60
labels, _ = ndi.label(low)
edge_labels = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
bg = np.isin(labels, edge_labels[edge_labels > 0])
fg = ~bg
fl, n = ndi.label(fg)
fg = fl == (np.argmax(np.bincount(fl.ravel())[1:]) + 1)  # the icon only, no stray specks

# Solid colours either side of the edge, carried outward to the band by nearest-pixel lookup.
inner = ndi.binary_erosion(fg, iterations=BAND + 1)
outer = ~ndi.binary_dilation(fg, iterations=BAND + 1)  # (not an erosion of ~fg: that eats the image border)
_, (iy, ix) = ndi.distance_transform_edt(~inner, return_indices=True)
_, (oy, ox) = ndi.distance_transform_edt(~outer, return_indices=True)
F = img[iy, ix]
B = img[oy, ox]

alpha = fg.astype(np.float64)
band = ~inner & ~outer
fb = F - B
den = (fb * fb).sum(-1)
solved = np.clip(((img - B) * fb).sum(-1) / np.maximum(den, 1e-6), 0, 1)
ok = band & (den > 400)  # enough contrast to solve; elsewhere keep the hard edge, softened below
alpha[ok] = solved[ok]
soft = ndi.gaussian_filter(fg.astype(np.float64), 0.7)
alpha[band & ~ok] = soft[band & ~ok]

# Un-mix the backdrop from edge colours; fall back to the solid icon colour where alpha is small.
colour = img.copy()
a = alpha[..., None]
unmixed = np.clip((img - (1 - a) * B) / np.maximum(a, 1e-6), 0, 255)
wgt = np.clip((a - 0.15) / 0.5, 0, 1)
colour[band] = (wgt * unmixed + (1 - wgt) * F)[band]

# Square crop centred on the tile. The tile is the icon's extent on the left, top and bottom; its
# right edge is read low down, below the rocket.
ys, xs = np.nonzero(alpha > 0.5)
left, top, bottom = xs.min(), ys.min(), ys.max()
row = int(top + 0.85 * (bottom - top))
right = np.nonzero(alpha[row] > 0.5)[0].max()
cx, cy = (left + right) / 2, (top + bottom) / 2
any_ys, any_xs = np.nonzero(alpha > 0.01)
half = max(np.abs(any_xs - cx).max(), np.abs(any_ys - cy).max()) + 6
x0, y0 = int(round(cx - half)), int(round(cy - half))
side = int(round(2 * half))
pad = max(0, -x0, -y0, x0 + side - w, y0 + side - h)
rgba = np.dstack([colour, alpha * 255])
if pad:
    rgba = np.pad(rgba, ((pad, pad), (pad, pad), (0, 0)))
    x0 += pad
    y0 += pad
crop = rgba[y0:y0 + side, x0:x0 + side]

# Resize premultiplied so the transparent surround cannot bleed into the edge.
premul = crop.copy()
premul[..., :3] *= premul[..., 3:4] / 255
chans = [Image.fromarray(premul[..., c].astype(np.float32), 'F').resize((SIZE, SIZE), Image.LANCZOS) for c in range(4)]
out = np.stack([np.asarray(ch) for ch in chans], -1)
out[..., 3] = np.clip(out[..., 3], 0, 255)
out[..., :3] = np.where(out[..., 3:4] > 0.5, out[..., :3] * 255 / np.maximum(out[..., 3:4], 1e-6), 0)
Image.fromarray(np.clip(out + 0.5, 0, 255).astype(np.uint8), 'RGBA').save(MASTER, optimize=True)

print(f'tile {left}-{right} x {top}-{bottom}, crop {side}px at ({x0 - pad}, {y0 - pad}), '
      f'tile fills {(right - left) / side:.0%} of the square -> {MASTER.name}')
