# Builds tray/app icons from Usage.png: tight crop, per-size stroke compensation, theme variants.
import struct, sys, io
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

SRC, OUT, PREVIEW = sys.argv[1], sys.argv[2], sys.argv[3]
src = np.asarray(Image.open(SRC).convert("RGBA")).astype(np.float64)
ink = (src[..., 3] / 255) * (1 - src[..., :3].mean(axis=2) / 255)

ys, xs = np.where(ink > 0.5)
cx, cy = (xs.min() + xs.max()) / 2, (ys.min() + ys.max()) / 2
side = int(max(xs.max() - xs.min(), ys.max() - ys.min())) + 2
x0, y0 = int(round(cx - side / 2)), int(round(cy - side / 2))
mask = ink[y0:y0 + side, x0:x0 + side]
binary = mask > 0.5

labels, count = ndimage.label(binary)
areas = ndimage.sum(binary, labels, range(1, count + 1))
centers = ndimage.center_of_mass(binary, labels, range(1, count + 1))
big = [i + 1 for i in np.argsort(areas)[::-1][:3]]
star = min(big, key=lambda l: np.hypot(centers[l - 1][0] - side / 2, centers[l - 1][1] - side / 2))
arcs = sorted((l for l in big if l != star), key=lambda l: areas[l - 1])
thin = arcs[0]
THIN_WIDTH = 36.0  # measured stroke width of the thin arc in source pixels
outside_thin = ndimage.distance_transform_edt(labels != thin)
outside_thick = ndimage.distance_transform_edt(labels != arcs[1])


def grow_for(size):
    inner = size - 2 * max(1, round(size * 0.05))
    k = inner / side
    # Keep the thin arc at least ~1.1px wide so it survives 16-24px rendering.
    return max(0.0, (1.1 / k - THIN_WIDTH) / 2) if size <= 32 else 0.0


def glyph(size):
    pad = max(1, round(size * 0.05))
    inner = size - pad * 2
    k = inner / side
    thin_shape = (outside_thin <= grow_for(size)) & (outside_thick >= 1.15 / k)
    # Keep at least ~1px of gap to the thick arc so the broken ring stays readable at tray sizes.
    shape = ((labels > 0) & (labels != thin)) | thin_shape
    alpha = np.asarray(Image.fromarray((shape * 255).astype(np.uint8)).resize((inner, inner), Image.Resampling.BOX),
                       dtype=np.float64) / 255
    if size <= 24:  # firmer edges at tray sizes
        alpha = np.clip((alpha - 0.5) * 1.2 + 0.5, 0, 1) * (alpha > 0.04)
    canvas = np.zeros((size, size))
    canvas[pad:pad + inner, pad:pad + inner] = alpha
    return canvas


def colored(alpha, rgb):
    out = np.zeros(alpha.shape + (4,), np.uint8)
    out[..., 0], out[..., 1], out[..., 2] = rgb
    out[..., 3] = np.round(alpha * 255)
    return Image.fromarray(out)


def tile(size):
    # Background drawn at 4x for smooth corners; glyph composited at 1x to keep its stroke compensation.
    big_size = size * 4
    img = Image.new("RGBA", (big_size, big_size), (0, 0, 0, 0))
    inset = round(big_size * 0.06)
    ImageDraw.Draw(img).rounded_rectangle((inset, inset, big_size - inset - 1, big_size - inset - 1),
                                          radius=big_size * 0.2, fill=(16, 16, 18, 255))
    img = img.resize((size, size), Image.Resampling.LANCZOS)
    inner = round(size * 0.58)
    img.alpha_composite(colored(glyph(inner), (246, 246, 248)), ((size - inner) // 2, (size - inner) // 2))
    return img


def dib(img):
    w, h = img.size
    px = np.asarray(img)[::-1, :, [2, 1, 0, 3]].tobytes()
    and_row = ((w + 31) // 32) * 4
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(px), 0, 0, 0, 0)
    return header + px + b"\0" * (and_row * h)


def write_ico(path, images):
    frames = []
    for img in images:
        if img.size[0] >= 256:
            buffer = io.BytesIO(); img.save(buffer, "PNG"); frames.append((img.size[0], buffer.getvalue()))
        else:
            frames.append((img.size[0], dib(img)))
    offset = 6 + 16 * len(frames)
    data = struct.pack("<HHH", 0, 1, len(frames))
    for size, blob in frames:
        data += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(data + b"".join(blob for _, blob in frames))


tray_sizes = [16, 20, 24, 32, 40, 48, 64]
glyphs = {s: glyph(s) for s in tray_sizes}
light = [colored(glyphs[s], (26, 26, 28)) for s in tray_sizes]
dark = [colored(glyphs[s], (255, 255, 255)) for s in tray_sizes]
app = [tile(s) for s in [16, 20, 24, 32, 40, 48, 64, 128, 256]]
write_ico(f"{OUT}/tray-light.ico", light)
write_ico(f"{OUT}/tray-dark.ico", dark)
write_ico(f"{OUT}/app.ico", app)

# Preview sheet: each tray size at 8x and 1x on light and dark taskbar colors.
rows = []
for images, bg in ((light, (243, 243, 243, 255)), (dark, (32, 32, 32, 255))):
    row = Image.new("RGBA", (sum(s * 8 + 16 for s in tray_sizes) + 16, 64 * 8 + 100), bg)
    x = 16
    for img in images:
        s = img.size[0]
        tile_bg = Image.new("RGBA", img.size, bg); tile_bg.alpha_composite(img)
        row.paste(tile_bg.resize((s * 8, s * 8), Image.Resampling.NEAREST), (x, 16))
        row.paste(tile_bg, (x, 16 + s * 8 + 4))
        x += s * 8 + 16
    rows.append(row)
sheet = Image.new("RGBA", (rows[0].width, rows[0].height * 2 + 290), (128, 128, 128, 255))
sheet.paste(rows[0], (0, 0)); sheet.paste(rows[1], (0, rows[0].height))
x = 16
for img in app:
    sheet.alpha_composite(img, (x, rows[0].height * 2 + 16)); x += img.size[0] + 16
sheet.save(PREVIEW)
print("thin grow per size:", {s: round(grow_for(s), 1) for s in tray_sizes if s <= 32})
