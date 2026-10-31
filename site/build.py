#!/usr/bin/env python3
"""Builds site/dist from the template (index.html, Slovak text in the HTML) and i18n.en.json.

Output: dist/index.html (Slovak, https://www.lab463.com/tracker/) and dist/en/index.html (English, .../tracker/en/), both fully
pre-rendered so that search engines and AI crawlers read the text of each language without running JavaScript. Adds canonical,
hreflang, Open Graph/Twitter tags and JSON-LD (SoftwareApplication, FAQPage, HowTo for the three guides), and puts a content
hash on every local asset URL (?v=...) so that a CDN never serves a stale file. Also writes the fragments for the shared
sitemap.xml, robots.txt and llms.txt of lab463.com (dist/seo/).
"""
import hashlib, html, json, os, re, shutil, sys

HERE = os.path.dirname(os.path.abspath(__file__))
DIST = os.path.join(HERE, 'dist')
BASE = 'https://www.lab463.com/tracker/'
URL = {'sk': BASE, 'en': BASE + 'en/'}

META = {
    'sk': {
        'title': 'PositionTracker – živé sledovanie pretekov na mape',
        'desc': 'Živé sledovanie behu a cyklistických pretekov na mape: poradie, odstupy a odhad príchodu do cieľa. Diváci nepotrebujú účet ani aplikáciu.',
        'og_alt': 'Riadenie pretekov v PositionTracker: mapa s jazdcami, poradie a hlásenia',
        'locale': 'sk_SK', 'locale_alt': 'en_US',
    },
    'en': {
        'title': 'PositionTracker – live race tracking on a map',
        'desc': 'Live tracking for running and cycling races on a map: standings, gaps and expected finish time. Spectators need no account and no app.',
        'og_alt': 'PositionTracker race control: a map with riders, standings and messages',
        'locale': 'en_US', 'locale_alt': 'sk_SK',
    },
}

EN = json.load(open(os.path.join(HERE, 'i18n.en.json'), encoding='utf-8'))
TEMPLATE = open(os.path.join(HERE, 'index.html'), encoding='utf-8').read()


def find_close(h, pos, tag):
    """Index of the </tag> that closes the element whose content starts at pos (same-name nesting is counted)."""
    depth, i = 1, pos
    pat = re.compile(r'<(/?)' + tag + r'\b[^>]*>')
    while True:
        m = pat.search(h, i)
        if not m:
            raise ValueError('unclosed <%s> at %d' % (tag, pos))
        depth += -1 if m.group(1) else 1
        if depth == 0:
            return m.start()
        i = m.end()


def translate(h):
    """Replaces the content of every data-i element and the data-i-aria / data-i-alt attributes by the English text."""
    out, i = [], 0
    start_tag = re.compile(r'<(\w+)\b([^>]*?)\sdata-i="([\w]+)"([^>]*)>')
    while True:
        m = start_tag.search(h, i)
        if not m:
            out.append(h[i:])
            break
        key = m.group(3)
        if key not in EN:
            raise KeyError('missing English text: ' + key)
        close = find_close(h, m.end(), m.group(1))
        out.append(h[i:m.end()])
        out.append(EN[key])
        i = close
    h = ''.join(out)

    def attr(m):
        tag, key, name = m.group(0), m.group(2), m.group(3)
        if key not in EN:
            raise KeyError('missing English text: ' + key)
        return re.sub(r'\b' + name + r'="[^"]*"', name + '="' + html.escape(EN[key], quote=True) + '"', tag, count=1)

    h = re.sub(r'<[^>]*\sdata-i-(aria)="(\w+)"[^>]*>', lambda m: attr_aria(m, EN), h)
    h = re.sub(r'<[^>]*\sdata-i-(alt)="(\w+)"[^>]*>', lambda m: attr_alt(m, EN), h)
    return h


def attr_aria(m, en):
    tag, key = m.group(0), m.group(2)
    if key not in en:
        raise KeyError('missing English text: ' + key)
    return re.sub(r'aria-label="[^"]*"', 'aria-label="' + html.escape(en[key], quote=True) + '"', tag, count=1)


def attr_alt(m, en):
    tag, key = m.group(0), m.group(2)
    if key not in en:
        raise KeyError('missing English text: ' + key)
    return re.sub(r'\salt="[^"]*"', ' alt="' + html.escape(en[key], quote=True) + '"', tag, count=1)


def text_of(fragment):
    t = re.sub(r'<[^>]+>', '', fragment)
    return re.sub(r'\s+', ' ', html.unescape(t)).strip()


def jsonld(h, lang):
    m = META[lang]
    spec = [text_of(x) for x in re.findall(r'<dt[^>]*>(.*?)</dt>\s*<dd[^>]*>(.*?)</dd>', h, flags=re.S)[0:0]]
    feats = [text_of(a) + ': ' + text_of(b) for a, b in re.findall(r'<dt[^>]*>(.*?)</dt>\s*<dd[^>]*>(.*?)</dd>', h, flags=re.S)]
    faq = [{'@type': 'Question', 'name': text_of(q), 'acceptedAnswer': {'@type': 'Answer', 'text': text_of(a)}}
           for q, a in re.findall(r'<details>\s*<summary[^>]*>(.*?)</summary>\s*<p[^>]*>(.*?)</p>\s*</details>', h, flags=re.S)]
    howtos = []
    for pid, name_sk, name_en in (('p-org', 'Ako zorganizovať sledovanie pretekov', 'How to set up live tracking for a race'),
                                  ('p-par', 'Ako sa zapojiť do sledovaného preteku', 'How to take part in a tracked race'),
                                  ('p-spe', 'Ako sledovať preteky ako divák', 'How to follow a race as a spectator')):
        i = h.index('id="%s"' % pid)
        j = h.find('class="panel"', i + 10)
        if j < 0:
            j = h.index('</section>', i)
        panel = h[i:j]
        steps = []
        for n, (title, para) in enumerate(re.findall(r'<li>\s*<div>\s*<h3[^>]*>(.*?)</h3>\s*<p[^>]*>(.*?)</p>', panel, flags=re.S), 1):
            steps.append({'@type': 'HowToStep', 'position': n, 'name': text_of(title), 'text': text_of(para),
                          'url': URL[lang] + '#guide-' + {'p-org': 'organizer', 'p-par': 'participant', 'p-spe': 'spectator'}[pid]})
        howtos.append({'@type': 'HowTo', 'name': name_sk if lang == 'sk' else name_en, 'inLanguage': lang, 'step': steps})
    graph = [
        {'@type': 'WebSite', '@id': BASE + '#website', 'name': 'PositionTracker', 'url': URL[lang], 'inLanguage': lang,
         'publisher': {'@type': 'Person', 'name': 'Martin Horný', 'url': 'https://www.lab463.com/'}},
        {'@type': 'WebPage', '@id': URL[lang] + '#webpage', 'url': URL[lang], 'name': m['title'], 'description': m['desc'], 'inLanguage': lang,
         'isPartOf': {'@id': BASE + '#website'}, 'primaryImageOfPage': {'@type': 'ImageObject', 'url': BASE + 'assets/og-image.jpg', 'width': 1200, 'height': 630}},
        {'@type': 'SoftwareApplication', '@id': BASE + '#app', 'name': 'PositionTracker', 'url': URL[lang], 'description': m['desc'],
         'applicationCategory': 'SportsApplication', 'operatingSystem': 'Web browser', 'inLanguage': ['sk', 'en'],
         'image': BASE + 'assets/og-image.jpg', 'featureList': feats,
         'isAccessibleForFree': True},
        {'@type': 'FAQPage', '@id': URL[lang] + '#faq', 'mainEntity': faq},
    ] + [dict(ht, **{'@id': URL[lang] + '#howto-' + str(k)}) for k, ht in enumerate(howtos)]
    data = {'@context': 'https://schema.org', '@graph': graph}
    return '<script type="application/ld+json">\n' + json.dumps(data, ensure_ascii=False, indent=1).replace('</', '<\\/') + '\n  </script>'


def file_hash(rel):
    p = os.path.join(HERE, rel)
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()[:8]


def version_assets(h):
    def sub(m):
        rel = m.group(2)
        return '%s="%s?v=%s"' % (m.group(1), rel, file_hash(rel))
    return re.sub(r'(href|src)="((?:vendor|assets)/[^"?#]+|styles\.css|app\.js|demo\.js|theme\.js|config\.js)"', sub, h)


def prefix_paths(h, prefix):
    if not prefix:
        return h
    return re.sub(r'(href|src)="((?:vendor|assets)/[^"]+|styles\.css[^"]*|app\.js[^"]*|demo\.js[^"]*|theme\.js[^"]*|config\.js[^"]*)"',
                  lambda m: '%s="%s%s"' % (m.group(1), prefix, m.group(2)), h)


def render(lang):
    h = TEMPLATE
    if lang == 'en':
        h = translate(h)
    # data-i attributes are build-time only
    h = re.sub(r'\sdata-i(?:-aria|-alt)?="[^"]*"', '', h)
    m = META[lang]
    h = version_assets(h)
    prefix = '' if lang == 'sk' else '../'
    h = prefix_paths(h, prefix)
    alt = ('<link rel="alternate" hreflang="sk" href="%s">\n  <link rel="alternate" hreflang="en" href="%s">\n'
           '  <link rel="alternate" hreflang="x-default" href="%s">') % (URL['sk'], URL['en'], URL['sk'])
    repl = {
        '[[lang]]': lang, '[[title]]': html.escape(m['title']), '[[desc]]': html.escape(m['desc'], quote=True), '[[url]]': URL[lang],
        '[[altlinks]]': alt, '[[locale]]': m['locale'], '[[locale_alt]]': m['locale_alt'], '[[og_alt]]': html.escape(m['og_alt'], quote=True),
        '[[sk_href]]': './' if lang == 'sk' else '../', '[[en_href]]': 'en/' if lang == 'sk' else './',
        '[[sk_cur]]': 'aria-current="true"' if lang == 'sk' else '', '[[en_cur]]': 'aria-current="true"' if lang == 'en' else '',
    }
    for k, v in repl.items():
        h = h.replace(k, v)
    h = h.replace('[[jsonld]]', jsonld(h, lang))
    assert '[[' not in h, re.findall(r'\[\[\w+\]\]', h)
    return h


def main():
    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    os.makedirs(os.path.join(DIST, 'en'))
    for f in ('styles.css', 'app.js', 'demo.js', 'theme.js', 'config.js'):
        shutil.copy(os.path.join(HERE, f), DIST)
    shutil.copytree(os.path.join(HERE, 'vendor'), os.path.join(DIST, 'vendor'))
    shutil.copytree(os.path.join(HERE, 'assets'), os.path.join(DIST, 'assets'))
    for lang, path in (('sk', 'index.html'), ('en', 'en/index.html')):
        open(os.path.join(DIST, path), 'w', encoding='utf-8').write(render(lang))
    seo = os.path.join(DIST, 'seo')
    os.makedirs(seo)
    open(os.path.join(seo, 'sitemap-fragment.xml'), 'w', encoding='utf-8').write(
        '  <url>\n    <loc>%s</loc>\n    <xhtml:link rel="alternate" hreflang="sk" href="%s"/>\n    <xhtml:link rel="alternate" hreflang="en" href="%s"/>\n'
        '    <xhtml:link rel="alternate" hreflang="x-default" href="%s"/>\n    <lastmod>{LASTMOD}</lastmod>\n  </url>\n  <url>\n    <loc>%s</loc>\n'
        '    <xhtml:link rel="alternate" hreflang="sk" href="%s"/>\n    <xhtml:link rel="alternate" hreflang="en" href="%s"/>\n'
        '    <xhtml:link rel="alternate" hreflang="x-default" href="%s"/>\n    <lastmod>{LASTMOD}</lastmod>\n  </url>\n'
        % (URL['sk'], URL['sk'], URL['en'], URL['sk'], URL['en'], URL['sk'], URL['en'], URL['sk']))
    print('built', DIST)


if __name__ == '__main__':
    main()
