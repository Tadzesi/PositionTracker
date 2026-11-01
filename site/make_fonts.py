#!/usr/bin/env python3
"""Creates the self-hosted fonts (replaces Google Fonts, which blocked rendering with two extra connections).

Downloads the OFL sources from github.com/google/fonts, keeps only the characters the product uses (Basic Latin, Latin-1 and
Latin Extended-A = Slovak, Czech, Polish, Hungarian, plus typographic punctuation) and writes subset woff2 files:
  site/fonts/archivo-roman.woff2, archivo-italic.woff2     variable (roman: weight 400-900; italic: width 62-75, weight 400-900), used by the site
  web/src/fonts/*.woff2                                     static Barlow Condensed + Titillium Web, used by the application
The results are committed; run this only when the character set or the fonts change.   Needs: pip install fonttools brotli
"""
import io, os, urllib.parse, urllib.request
from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = 'https://raw.githubusercontent.com/google/fonts/main/ofl/'
UNICODES = ([*range(0x20, 0x7F), *range(0xA0, 0x180), 0x2013, 0x2014, 0x2018, 0x2019, 0x201A, 0x201C, 0x201D, 0x201E, 0x2022,
             0x2026, 0x2032, 0x2033, 0x20AC, 0x2190, 0x2191, 0x2192, 0x2193, 0x2212, 0x2248])


def fetch(path):
    return urllib.request.urlopen(RAW + urllib.parse.quote(path)).read()


def build(data, out, limits=None):
    font = TTFont(io.BytesIO(data))
    if limits:
        font = instancer.instantiateVariableFont(font, limits)
    opts = subset.Options()
    opts.flavor = 'woff2'
    opts.layout_features = ['kern', 'liga', 'calt', 'ccmp', 'locl', 'mark', 'mkmk']
    opts.notdef_outline = True
    opts.name_IDs = [1, 2, 3, 4, 6]
    sub = subset.Subsetter(opts)
    sub.populate(unicodes=UNICODES)
    sub.subset(font)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    font.flavor = 'woff2'
    font.save(out)
    print('%7d  %s' % (os.path.getsize(out), os.path.relpath(out, os.path.join(HERE, '..'))))


def main():
    site = os.path.join(HERE, 'fonts')
    # the site sets font-stretch 100% only on roman text and 62-75% only on italic display text, so each file keeps just that range
    build(fetch('archivo/Archivo[wdth,wght].ttf'), os.path.join(site, 'archivo-roman.woff2'), {'wdth': 100, 'wght': (400, 900)})
    build(fetch('archivo/Archivo-Italic[wdth,wght].ttf'), os.path.join(site, 'archivo-italic.woff2'), {'wdth': (62, 75), 'wght': (400, 900)})
    app = os.path.join(HERE, '..', 'web', 'src', 'fonts')
    for name, src in (
        ('barlow-condensed-500', 'barlowcondensed/BarlowCondensed-Medium.ttf'),
        ('barlow-condensed-700', 'barlowcondensed/BarlowCondensed-Bold.ttf'),
        ('barlow-condensed-800', 'barlowcondensed/BarlowCondensed-ExtraBold.ttf'),
        ('barlow-condensed-600i', 'barlowcondensed/BarlowCondensed-SemiBoldItalic.ttf'),
        ('barlow-condensed-800i', 'barlowcondensed/BarlowCondensed-ExtraBoldItalic.ttf'),
        ('barlow-condensed-900i', 'barlowcondensed/BarlowCondensed-BlackItalic.ttf'),
        ('titillium-web-400', 'titilliumweb/TitilliumWeb-Regular.ttf'),
        ('titillium-web-600', 'titilliumweb/TitilliumWeb-SemiBold.ttf'),
        ('titillium-web-700', 'titilliumweb/TitilliumWeb-Bold.ttf'),
    ):
        build(fetch(src), os.path.join(app, name + '.woff2'))


if __name__ == '__main__':
    main()
