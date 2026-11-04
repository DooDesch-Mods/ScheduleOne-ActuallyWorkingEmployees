"""Counts reddish pixels in one pixel column of a screenshot.

Used by awe-app-render.ps1 on the shot taken after a typed number was set to its minimum: the green note bar must not
carry the red top line of the error note. Prints "red=<count> at=<y list>"; the caller decides.

Usage: python -I red-line.py <png> <x> <y0> <y1>
"""
import sys

from PIL import Image


def reddish(p):
    r, g, b = p
    return r >= 60 and r > g + 30 and r > b + 20


def main():
    if len(sys.argv) != 5:
        print('usage: red-line.py <png> <x> <y0> <y1>', file=sys.stderr)
        return 2
    path, x, y0, y1 = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    im = Image.open(path).convert('RGB')
    hits = [y for y in range(y0, min(y1, im.size[1])) if reddish(im.getpixel((x, y)))]
    print('red=%d at=%s' % (len(hits), ','.join(str(y) for y in hits[:10])))
    return 0


if __name__ == '__main__':
    sys.exit(main())
