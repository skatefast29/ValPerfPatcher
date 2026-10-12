"""Builds a Thunderstore-format zip that Gale can import (Profile > Import > Local mod).

Usage: python package.py   (after: dotnet build -c Release)
Output: dist/ValPerfPatcher-<version>.zip
"""
import json
import os
import re
import struct
import zipfile
import zlib

ROOT = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(ROOT, "bin", "Release", "net48", 'ValPerfPatcher.dll')
VERSION = re.search(r'<Version>([^<]+)</Version>', open(os.path.join(ROOT, 'ValPerfPatcher.csproj'), encoding='utf-8').read()).group(1)


def icon_png(size=256):
    """A simple 256x256 icon: a teal lightning bolt on a dark background (the monitor has orange bars)."""
    bolt = [(150, 24), (60, 142), (118, 142), (96, 232), (196, 104), (136, 104), (166, 24)]
    bg, fg = (24, 26, 32), (40, 200, 190)

    def inside(px, py):
        hit = False
        for (x1, y1), (x2, y2) in zip(bolt, bolt[1:] + bolt[:1]):
            if (y1 > py) != (y2 > py) and px < x1 + (py - y1) * (x2 - x1) / (y2 - y1):
                hit = not hit
        return hit

    rows = []
    for y in range(size):
        row = bytearray([0])  # filter type 0
        for x in range(size):
            # 4x4 samples per pixel for smooth edges
            a = sum(inside(x + (i + 0.5) / 4, y + (j + 0.5) / 4) for i in range(4) for j in range(4)) / 16
            row += bytes(round(bg[c] + (fg[c] - bg[c]) * a) for c in range(3))
        rows.append(bytes(row))
    raw = zlib.compress(b''.join(rows), 9)

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)) + chunk(b'IDAT', raw) + chunk(b'IEND', b'')


manifest = {
    "name": "ValPerfPatcher",
    "version_number": VERSION,
    "website_url": "https://github.com/skatefast29/ValPerfPatcher",
    "description": "Runtime fixes for performance hot spots in popular mods and the base game (less garbage, faster startup and world loading), found with ValPerfMonitor.",
    "dependencies": ["denikson-BepInExPack_Valheim-5.4.2351"],
}

os.makedirs(os.path.join(ROOT, 'dist'), exist_ok=True)
out = os.path.join(ROOT, 'dist', f'ValPerfPatcher-{VERSION}.zip')
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('manifest.json', json.dumps(manifest, indent=2))
    z.writestr('icon.png', icon_png())
    z.write(os.path.join(ROOT, 'README.md'), 'README.md')
    z.write(os.path.join(ROOT, 'CHANGELOG.md'), 'CHANGELOG.md')
    z.write(DLL, 'plugins/ValPerfPatcher.dll')
print(out)
