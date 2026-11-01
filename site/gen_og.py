"""Creates assets/og-image.jpg (1200x630) for link previews: the headline on the brand navy next to a real screenshot."""
from PIL import Image, ImageDraw, ImageFont
import os
HERE = os.path.dirname(os.path.abspath(__file__))
W, H = 1200, 630
im = Image.new('RGB', (W, H), (10, 31, 68))
shot = Image.open(os.path.join(HERE, 'shots-src/control-light.png')).convert('RGB')
# the map and the standings of the race-control view, scaled to the right 60 % of the card
target_w = 640
crop = shot.crop((200, 520, 1700, 520 + int(1500 * H / target_w))).resize((target_w, H), Image.LANCZOS)
im.paste(crop, (W - target_w, 0))
d = ImageDraw.Draw(im)
d.rectangle([W - target_w - 8, 0, W - target_w - 1, H], fill=(255, 75, 43))
font_path = next((p for p in (r'C:\Windows\Fonts\impact.ttf', r'C:\Windows\Fonts\arialbd.ttf') if os.path.exists(p)), None)
big = ImageFont.truetype(font_path, 76) if font_path else ImageFont.load_default()
small = ImageFont.truetype(r'C:\Windows\Fonts\arialbd.ttf', 30) if os.path.exists(r'C:\Windows\Fonts\arialbd.ttf') else ImageFont.load_default()
y = 90
for line, color in (('EVERY RIDER.', (255, 255, 255)), ('EVERY SECOND.', (255, 255, 255)), ('LIVE.', (255, 75, 43))):
    d.text((50, y), line, font=big, fill=color)
    y += 96
d.text((52, H - 90), 'PositionTracker', font=small, fill=(199, 213, 242))
d.text((52, H - 52), 'lab463.com/tracker', font=small, fill=(157, 176, 214))
im.save(os.path.join(HERE, 'assets/og-image.jpg'), 'JPEG', quality=86, optimize=True)
print('og-image', os.path.getsize(os.path.join(HERE, 'assets/og-image.jpg')) // 1024, 'KB')
