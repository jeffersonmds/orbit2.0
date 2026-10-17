# Gera o logo do Orbit 2.0 (a ilha atravessando o O da órbita, com a lua) em todos os tamanhos:
#   python Orbit/Assets/logo.py
# Saída: Orbit/Assets/orbit.ico (exe, bandeja, instalador) e os ícones do PWA em Bridge/wwwroot.
# Desenha em 4x e reduz (antialias). Precisa do Pillow (pip install pillow).
import math
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
WWW = os.path.join(HERE, "..", "Bridge", "wwwroot")

BG = (14, 14, 16, 255)          # mesmo fundo do PWA (--bg)
ACC = (76, 155, 240, 255)       # azul (--accent do PWA)
ACC2 = (124, 92, 255, 255)      # violeta
INK = (242, 242, 242, 255)
SS = 4


def gradient(n):
    g = Image.new("RGBA", (n, n))
    px = g.load()
    for y in range(n):
        for x in range(n):
            t = (x + y) / (2 * n)
            px[x, y] = tuple(int(ACC[i] + (ACC2[i] - ACC[i]) * t) for i in range(4))
    return g


def paint(im, mask, fill):
    layer = fill if isinstance(fill, Image.Image) else Image.new("RGBA", im.size, fill)
    out = Image.new("RGBA", im.size, (0, 0, 0, 0))
    out.paste(layer, (0, 0), mask)
    im.alpha_composite(out)


def logo(n, corner=0.225):
    """corner = raio do fundo em fração do lado (0 = quadrado cheio, pro iOS arredondar sozinho)."""
    N = n * SS
    c = N / 2
    im = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    ImageDraw.Draw(im).rounded_rectangle([0, 0, N - 1, N - 1], radius=int(N * corner), fill=BG)
    grad = gradient(N)

    # O da órbita: metade de cima atrás da ilha, metade de baixo na frente.
    R, w = N * 0.27, int(N * 0.08)
    ring = Image.new("L", (N, N), 0)
    ImageDraw.Draw(ring).ellipse([c - R, c - R, c + R, c + R], outline=255, width=w)
    paint(im, ring, grad)

    # A ilha, com um respiro do fundo em volta pra separar do anel.
    pw, ph = N * 0.74, N * 0.16
    halo = Image.new("L", (N, N), 0)
    ImageDraw.Draw(halo).rounded_rectangle(
        [c - pw / 2 - w * .45, c - ph / 2 - w * .45, c + pw / 2 + w * .45, c + ph / 2 + w * .45], radius=ph, fill=255)
    paint(im, halo, BG)
    pill = Image.new("L", (N, N), 0)
    ImageDraw.Draw(pill).rounded_rectangle([c - pw / 2, c - ph / 2, c + pw / 2, c + ph / 2], radius=ph / 2, fill=255)
    paint(im, pill, INK)

    front = ring.copy()
    ImageDraw.Draw(front).rectangle([0, 0, N, c + ph / 2], fill=0)
    paint(im, front, grad)

    # A lua no anel, com respiro.
    a = math.radians(-50)
    mx, my = c + R * math.cos(a), c + R * math.sin(a)
    d = ImageDraw.Draw(im)
    r = N * 0.06
    d.ellipse([mx - r, my - r, mx + r, my + r], fill=BG)
    r = N * 0.045
    d.ellipse([mx - r, my - r, mx + r, my + r], fill=INK)
    return im.resize((n, n), Image.LANCZOS)


if __name__ == "__main__":
    sizes = [16, 24, 32, 48, 64, 128, 256]
    big = logo(256)
    big.save(os.path.join(HERE, "orbit.ico"), sizes=[(s, s) for s in sizes],
             append_images=[logo(s) for s in sizes if s != 256])
    logo(192).save(os.path.join(WWW, "icon-192.png"))
    logo(512).save(os.path.join(WWW, "icon-512.png"))
    logo(180, corner=0).convert("RGB").save(os.path.join(WWW, "apple-touch-icon.png"))  # iOS: sem alfa, sem cantos
    print("ok")
