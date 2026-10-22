"""
Genera la grafica dell'installer (da rilanciare solo se cambia il logo):
  - splash.png          immagine mostrata da Setup.exe durante l'installazione
  - msi-banner.bmp      striscia in alto nelle finestre dell'installer .msi (493x58)
  - msi-dialog.bmp      sfondo delle pagine Benvenuto/Fine dell'installer .msi (493x312)
  - ../Assets/app.ico   icona dell'app in tutte le dimensioni (16-256)

Uso:  python Installer/make_installer_art.py   (dalla cartella DownloadManager)
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent
LOGO = Image.open(ROOT.parent / "Assets" / "logo.png").convert("RGBA")
FONTS = Path("C:/Windows/Fonts")

ACCENT = (59, 130, 246)       # blu dell'app
BG_TOP = (10, 17, 32)
BG_BOTTOM = (19, 33, 61)
TEXT = (241, 245, 249)
MUTED = (148, 163, 184)
FAINT = (100, 116, 139)


def font(name, size):
    for candidate in (name, "segoeui.ttf"):
        try:
            return ImageFont.truetype(str(FONTS / candidate), size)
        except OSError:
            continue
    return ImageFont.load_default()


def gradient(w, h, top, bottom):
    img = Image.new("RGB", (w, h), top)
    px = img.load()
    for y in range(h):
        for x in range(w):
            t = (y / max(1, h - 1)) * 0.75 + (x / max(1, w - 1)) * 0.25
            px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return img


def glow(size, color, radius, alpha):
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = size[0] // 2, size[1] // 2
    d.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=color + (alpha,))
    return layer.filter(ImageFilter.GaussianBlur(radius * 0.55))


def paste_logo(canvas, size, center):
    logo = LOGO.resize((size, size), Image.LANCZOS)
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    sx, sy = center[0] - size // 2, center[1] - size // 2
    shadow.paste((0, 0, 0, 120), (sx + 4, sy + 8, sx + size + 4, sy + size + 8), logo.split()[3])
    shadow = shadow.filter(ImageFilter.GaussianBlur(10))
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(logo, (sx, sy))


def centered(draw, y, parts, fnt, width):
    """parts: [(testo, colore)] sulla stessa riga, centrati insieme."""
    total = sum(draw.textlength(t, font=fnt) for t, _ in parts)
    x = (width - total) / 2
    for text, color in parts:
        draw.text((x, y), text, font=fnt, fill=color)
        x += draw.textlength(text, font=fnt)


# ------------------------------------------------------------------ splash di Setup.exe
W, H = 640, 380
splash = gradient(W, H, BG_TOP, BG_BOTTOM).convert("RGBA")
splash.alpha_composite(glow((W, H), ACCENT, 150, 70), (0, -70))
paste_logo(splash, 124, (W // 2, 112))
d = ImageDraw.Draw(splash)
centered(d, 196, [("DDL ", ACCENT), ("Download Manager", TEXT)], font("Inter-SemiBold.ttf", 36), W)
centered(d, 248, [("Anime  ·  Film e Serie  ·  SFTP  ·  Videobox", MUTED)], font("Inter-Regular.ttf", 15), W)
centered(d, 318, [("Installazione in corso…", FAINT)], font("Inter-Regular.ttf", 13), W)
# bordo sottile
d.rectangle((0, 0, W - 1, H - 1), outline=(37, 52, 82))
splash.convert("RGB").save(ROOT / "splash.png")

# ------------------------------------------------------------------ banner delle finestre .msi
# Windows scrive il titolo della pagina a sinistra in nero: fondo chiaro, logo a destra
bw, bh = 493, 58
banner = Image.new("RGBA", (bw, bh), (255, 255, 255, 255))
banner.alpha_composite(LOGO.resize((40, 40), Image.LANCZOS), (bw - 52, 9))
db = ImageDraw.Draw(banner)
db.rectangle((0, bh - 3, bw, bh), fill=ACCENT)
banner.convert("RGB").save(ROOT / "msi-banner.bmp")

# ------------------------------------------------------------------ sfondo pagine Benvenuto/Fine
# Windows scrive i testi nella parte destra (bianca); a sinistra il pannello grafico
dw, dh, panel = 493, 312, 164
dialog = Image.new("RGBA", (dw, dh), (255, 255, 255, 255))
side = gradient(panel, dh, BG_TOP, BG_BOTTOM).convert("RGBA")
side.alpha_composite(glow((panel, dh), ACCENT, 70, 80), (0, -40))
paste_logo(side, 84, (panel // 2, 116))
ds = ImageDraw.Draw(side)
f1 = font("Inter-SemiBold.ttf", 15)
f2 = font("Inter-Regular.ttf", 11)
for i, (text, fnt, color) in enumerate([("DDL", f1, ACCENT), ("Download Manager", f1, TEXT)]):
    tw = ds.textlength(text, font=fnt)
    ds.text(((panel - tw) / 2, 176 + i * 20), text, font=fnt, fill=color)
tag = "Anime · Film · SFTP"
ds.text(((panel - ds.textlength(tag, font=f2)) / 2, 228), tag, font=f2, fill=MUTED)
dialog.alpha_composite(side, (0, 0))
ImageDraw.Draw(dialog).rectangle((panel, 0, panel + 2, dh), fill=ACCENT)
dialog.convert("RGB").save(ROOT / "msi-dialog.bmp")

# ------------------------------------------------------------------ icona multi-dimensione
LOGO.save(ROOT.parent / "Assets" / "app.ico", format="ICO",
          sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

print("Grafica installer generata in", ROOT)
