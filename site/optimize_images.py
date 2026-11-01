#!/usr/bin/env python3
"""Creates the responsive screenshots: shots-src/<name>.(png|webp) -> assets/shots/<name>-<width>.(avif|webp).

The page used to ship the 2720 px originals (3.5 MB). Now build.py writes <picture> with srcset from the files made here, so a phone
downloads a 390-1280 px AVIF (tens of KB). The results are committed; run this when a screenshot in shots-src/ changes.
Needs: pip install pillow  (with AVIF support, Pillow >= 11.3)
"""
import glob, os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, 'shots-src')
OUT = os.path.join(HERE, 'assets', 'shots')
LANDSCAPE = (640, 1280, 1920)   # shown at up to ~900 CSS px: 1x, 2x and phones with a high pixel ratio
PORTRAIT = (390, 780)           # phone screenshots are shown at 270-300 CSS px


def main():
    os.makedirs(OUT, exist_ok=True)
    for old in glob.glob(os.path.join(OUT, '*')):
        os.remove(old)
    for path in sorted(glob.glob(os.path.join(SRC, '*'))):
        name = os.path.splitext(os.path.basename(path))[0]
        im = Image.open(path).convert('RGB')
        widths = PORTRAIT if im.height > im.width else LANDSCAPE
        for w in [x for x in widths if x < im.width] + ([im.width] if im.width <= widths[-1] else []):
            r = im if w == im.width else im.resize((w, round(im.height * w / im.width)), Image.LANCZOS)
            r.save(os.path.join(OUT, '%s-%d.avif' % (name, w)), quality=52, speed=4)
            r.save(os.path.join(OUT, '%s-%d.webp' % (name, w)), quality=80, method=6)
    total = sum(os.path.getsize(p) for p in glob.glob(os.path.join(OUT, '*')))
    print('%d files, %d KB' % (len(os.listdir(OUT)), total // 1024))


if __name__ == '__main__':
    main()
