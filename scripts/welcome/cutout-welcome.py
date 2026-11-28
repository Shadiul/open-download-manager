# Cuts the four welcome-step illustrations out of their sheet (a 2 x 2 sheet on a flat near-black
# backdrop, scripts/welcome/source/sheet-dark.jpg) into transparent PNGs in windows/OpenDownloadManager/Assets/welcome:
#   top-left     palette and settings window   -> look.png      (step 1, Look)
#   top-right    files raining into a folder   -> folder.png    (step 2, Folder)
#   bottom-left  puzzle piece and browsers     -> browsers.png  (step 3, Browsers)
#   bottom-right checklist and rocket          -> ready.png     (You're ready)
# sheet-checker-reference.jpg is the same sheet on a checkerboard painted into the pixels (not real
# transparency); it is kept only as a reference.
#
# How (colour-difference matting, as for the app icon's smoke): the backdrop is one flat colour, sampled
# from the sheet's border. A pixel's alpha is how far it has risen above the backdrop in its brightest
# channel, reaching 1 at FULL levels; its colour is then un-mixed from the backdrop. Glass, glows and
# paint splashes stay see-through and soft, solid parts stay solid, and there is no dark fringe.
# Each picture is trimmed to what is drawn, centred in a square and saved at SIZE px (the step shows it
# at 96, so this is sharp up to 300% scaling).
#
# Needs Python 3 with Pillow and NumPy.  Usage: python scripts\welcome\cutout-welcome.py
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'windows' / 'OpenDownloadManager' / 'Assets' / 'welcome'
SHEET = Path(__file__).resolve().parent / 'source' / 'sheet-dark.jpg'
PARTS = {'look': (0, 0), 'folder': (0, 1), 'browsers': (1, 0), 'ready': (1, 1)}
FULL = 120     # levels above the backdrop at which a pixel is opaque
NOISE = 6      # JPEG noise in the backdrop, ignored
MARGIN = 0.02  # of the square, around what is drawn
SIZE = 320


def main():
    img = np.asarray(Image.open(SHEET).convert('RGB')).astype(np.float64)
    h, w, _ = img.shape
    border = np.concatenate([img[:24].reshape(-1, 3), img[-24:].reshape(-1, 3), img[:, :24].reshape(-1, 3), img[:, -24:].reshape(-1, 3)])
    back = np.median(border, axis=0)

    rise = np.clip(img - back, 0, None).max(axis=2)
    alpha = np.clip((rise - NOISE) / (FULL - NOISE), 0, 1)
    safe = np.maximum(alpha, 1e-3)[..., None]
    colour = np.clip(back + (img - back) / safe, 0, 255)

    for name, (row, col) in PARTS.items():
        ys, xs = slice(row * h // 2, (row + 1) * h // 2), slice(col * w // 2, (col + 1) * w // 2)
        a, c = alpha[ys, xs], colour[ys, xs]
        yy, xx = np.nonzero(a > 0.02)
        y0, y1, x0, x1 = yy.min(), yy.max() + 1, xx.min(), xx.max() + 1
        side = int(max(y1 - y0, x1 - x0) * (1 + 2 * MARGIN))
        cy, cx = (y0 + y1) // 2, (x0 + x1) // 2
        rgba = np.zeros((side, side, 4))
        top, left = cy - side // 2, cx - side // 2
        sy0, sx0 = max(top, 0), max(left, 0)
        sy1, sx1 = min(top + side, a.shape[0]), min(left + side, a.shape[1])
        rgba[sy0 - top:sy1 - top, sx0 - left:sx1 - left, :3] = c[sy0:sy1, sx0:sx1]
        rgba[sy0 - top:sy1 - top, sx0 - left:sx1 - left, 3] = a[sy0:sy1, sx0:sx1] * 255
        out = Image.fromarray(rgba.round().astype(np.uint8), 'RGBA')
        # Premultiplied while scaling, so see-through edges don't pick up the hidden colours.
        out = out.convert('RGBa').resize((SIZE, SIZE), Image.LANCZOS).convert('RGBA')
        out.save(ASSETS / f'{name}.png', optimize=True)
        print(name, (x0, y0, x1, y1), side)


if __name__ == '__main__':
    main()
