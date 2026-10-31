#!/usr/bin/env python3
"""Builds the launcher's own trophy pack: stardew/sce_sys/trophy/STDV00001_00/TROPHY.TRP.

The pack is unsigned (the signature lines are placeholders, as in Rinnegatamante's ports): it only works with the
NoTrpDrm plugin, which makes SceShell skip the NP signature checks. Its icons are drawn here (a cup per grade), so
nothing of the game is in the VPK. build-stardew.sh copies sce_sys into the VPK as it is; run this script again only
when the list changes, and keep it in step with StardewValley.SDKs/VitaTrophies.cs (ids and grades).

Format (checked against not_a_hero-vita's livearea/TROPHY.TRP): a 0x40-byte big-endian header (magic DCA24D00,
version 2, file size, entry count, entry size 0x40, flags, SHA-1 of the whole file with this field zeroed), 0x40-byte
entries (name, offset, size), then the files at 16-byte alignment: TROPCONF.SFM, TROP.SFM (plain XML), ICON0.PNG
(320x176, the manual's first picture scaled and cropped) and TROPnnn.PNG (240x240).

Usage: python make-trophies.py [--no-platinum]
"""

import hashlib
import io
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

COMM_ID = "STDV00001_00"
SET_VERSION = "01.00"
TITLE = "Stardew Valley Enhanced"
TITLE_DETAIL = "Achievements of Stardew Valley 1.6 as trophies (unofficial homebrew port)."

# id, grade (P/G/S/B), name, detail. Id 0 is the platinum, which the system awards once every other trophy is
# unlocked. Ids 1-39 are Data/Achievements in VitaTrophies.cs order, 40-49 the Steam-only achievements.
# Grades: official PS4/PS Vita list (see README.md); Perfection is gold here (the C# side shows it as the platinum).
TROPHIES = [
    (0, "P", "Stardew Valley Enhanced", "Unlock every other trophy."),
    (1, "B", "Greenhorn", "Earn 15,000g"),
    (2, "B", "Cowpoke", "Earn 50,000g"),
    (3, "B", "Homesteader", "Earn 250,000g"),
    (4, "B", "Millionaire", "Earn 1,000,000g"),
    (5, "G", "Legend", "Earn 10,000,000g"),
    (6, "S", "A Complete Collection", "Complete the museum collection."),
    (7, "B", "A New Friend", "Reach a 5-heart friend level with someone."),
    (8, "B", "Best Friends", "Reach a 10-heart friend level with someone."),
    (9, "S", "The Beloved Farmer", "Reach a 10-heart friend level with 8 people."),
    (10, "B", "Cliques", "Reach a 5-heart friend level with 4 people."),
    (11, "B", "Networking", "Reach a 5-heart friend level with 10 people."),
    (12, "B", "Popular", "Reach a 5-heart friend level with 20 people."),
    (13, "B", "Cook", "Cook 10 different recipes."),
    (14, "S", "Sous Chef", "Cook 25 different recipes."),
    (15, "S", "Gourmet Chef", "Cook every recipe."),
    (16, "B", "Moving Up", "Upgrade your house."),
    (17, "B", "Living Large", "Upgrade your house to the maximum size."),
    (18, "B", "D.I.Y.", "Craft 15 different items."),
    (19, "S", "Artisan", "Craft 30 different items."),
    (20, "G", "Craft Master", "Craft every item."),
    (21, "B", "Fisherman", "Catch 10 different fish."),
    (22, "B", "Ol' Mariner", "Catch 24 different fish."),
    (23, "S", "Master Angler", "Catch every fish."),
    (24, "B", "Mother Catch", "Catch 100 fish."),
    (25, "B", "Treasure Trove", "Donate 40 different items to the museum."),
    (26, "B", "Gofer", "Complete 10 'Help Wanted' requests."),
    (27, "S", "A Big Help", "Complete 40 'Help Wanted' requests."),
    (28, "B", "Polyculture", "Ship 15 of each crop."),
    (29, "B", "Monoculture", "Ship 300 of one crop."),
    (30, "G", "Full Shipment", "Ship every item."),
    (31, "S", "Well-Read", "Read every book."),
    (32, "B", "Two Thumbs Up", "See a movie."),
    (33, "B", "Blue Ribbon", "Get 1st place in the Stardew Valley Fair competition."),
    (34, "B", "An Unforgettable Soup", "Delight the Governor."),
    (35, "B", "Good Neighbors", "Help your forest neighbors grow their family."),
    (36, "B", "A Distant Shore", "Reach Ginger Island."),
    (37, "S", "Danger In The Deep", "Reach the bottom of the 'dangerous' mines."),
    (38, "S", "Infinite Power", "Obtain the most powerful weapon."),
    (39, "G", "Perfection", "Reach 100% game completion."),
    (40, "B", "Local Legend", "Restore the Pelican Town Community Center."),
    (41, "B", "Joja Co. Member Of The Year", "Become a Joja Co. member and purchase a community center development."),
    (42, "B", "Prairie King", "Beat 'Journey Of The Prairie King'."),
    (43, "S", "Fector's Challenge", "Beat 'Journey Of The Prairie King' without dying."),
    (44, "B", "Full House", "Get married and have two kids."),
    (45, "B", "The Bottom", "Reach the lowest level of the mines."),
    (46, "S", "Protector Of The Valley", "Complete all of the Adventure Guild Monster Slayer goals."),
    (47, "B", "Singular Talent", "Reach level 10 in a skill."),
    (48, "S", "Master Of The Five Ways", "Reach level 10 in every skill."),
    (49, "G", "Mystery Of The Stardrops", "Find every stardrop."),
]

# The same 16x16 cup as VitaTrophies.CreateIcons: o outline, # body, h highlight, d shade.
CUP = [
    "................",
    "...oooooooooo...",
    ".oooh######dooo.",
    ".o.oh######do.o.",
    ".o.oh######do.o.",
    "..ooh######doo..",
    "....oh####do....",
    ".....oh##do.....",
    "......o##o......",
    "......o##o......",
    ".....oo##oo.....",
    "....o######o....",
    "....oooooooo....",
    "................",
    "................",
    "................",
]

PALETTES = {
    "B": [(70, 40, 15), (205, 127, 50), (240, 180, 110), (140, 80, 30)],
    "S": [(60, 60, 70), (192, 192, 200), (245, 245, 250), (130, 130, 140)],
    "G": [(90, 60, 10), (230, 180, 40), (255, 235, 130), (170, 120, 20)],
    "P": [(40, 55, 80), (170, 200, 230), (240, 250, 255), (110, 140, 170)],
}

SIGNATURE = "<!--Sce-Np-Trophy-Signature: " + "x" * 320 + "-->\n"


def cup_image(grade, scale):
    image = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
    for y, row in enumerate(CUP):
        for x, c in enumerate(row):
            if c != ".":
                image.putpixel((x, y), PALETTES[grade]["o#hd".index(c)] + (255,))
    return image.resize((16 * scale, 16 * scale), Image.NEAREST)


def png_bytes(image):
    out = io.BytesIO()
    image.save(out, "PNG", optimize=True)
    return out.getvalue()


def trophy_icon(grade):
    # A dark tile with the grade's cup at 13x (the cup's 13 rows fill most of it).
    image = Image.new("RGBA", (240, 240), (28, 30, 38, 255))
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 228, 239, 239], fill=PALETTES[grade][1] + (255,))
    cup = cup_image(grade, 13)
    image.alpha_composite(cup, (round((240 - 208) / 2), 22))
    return png_bytes(image)


def title_icon():
    # The startup picture (sce_sys/pic0.png, 960x544) scaled to 320x176. Its aspect
    # ratio (1.765) is a little narrower than 320x176 (1.818), so scale to the width and crop top/bottom
    # so the logo and mountains stay centred and undistorted. Falls back to manual/001.png or drawn cups.
    here = os.path.dirname(os.path.abspath(__file__))
    src = os.path.join(here, "..", "sce_sys", "pic0.png")
    if not os.path.exists(src):
        src = os.path.join(here, "..", "sce_sys", "manual", "001.png")
    if os.path.exists(src):
        image = Image.open(src).convert("RGBA")
        w, h = 320, 176
        scale = max(w / image.width, h / image.height)
        nw, nh = round(image.width * scale), round(image.height * scale)
        image = image.resize((nw, nh), Image.LANCZOS)
        left, top = (nw - w) // 2, (nh - h) // 2
        return png_bytes(image.crop((left, top, left + w, top + h)))
    image = Image.new("RGBA", (320, 176), (28, 30, 38, 255))
    for i, grade in enumerate("BSGP"):
        image.alpha_composite(cup_image(grade, 4), (24 + i * 72, 24))
    draw = ImageDraw.Draw(image)
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", 22)
    except OSError:
        try:
            font = ImageFont.truetype("arialbd.ttf", 22)
        except OSError:
            font = ImageFont.load_default()
    draw.text((160, 128), TITLE, font=font, fill=(240, 240, 240, 255), anchor="mm")
    return png_bytes(image)


def xml_escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def sfm(trophies, with_names):
    lines = [
        '<trophyconf version="1.1" platform="psp2" policy="small">',
        " <npcommid>%s</npcommid>" % COMM_ID,
        " <trophyset-version>%s</trophyset-version>" % SET_VERSION,
        ' <parental-level license-area="default">0</parental-level>',
    ]
    if with_names:
        lines.append(" <title-name>%s</title-name>" % xml_escape(TITLE))
        lines.append(" <title-detail>%s</title-detail>" % xml_escape(TITLE_DETAIL))
    has_platinum = any(grade == "P" for _, grade, _, _ in trophies)
    for trophy_id, grade, name, detail in trophies:
        pid = "000" if has_platinum and grade != "P" else "-1"
        head = ' <trophy id="%03d" hidden="no" ttype="%s" pid="%s"' % (trophy_id, grade, pid)
        if with_names:
            lines.append(head + ">")
            lines.append("  <name>%s</name>" % xml_escape(name))
            lines.append("  <detail>%s</detail>" % xml_escape(detail))
            lines.append(" </trophy>")
        else:
            lines.append(head + "/>")
    lines.append("</trophyconf>")
    return (SIGNATURE + "\n".join(lines) + "\n").encode("utf-8")


def build_trp(files):
    header_size = 0x40
    entry_size = 0x40
    offset = header_size + entry_size * len(files)
    entries = b""
    data = b""
    for name, content in files:
        pad = (-offset) % 16
        data += b"\0" * pad
        offset += pad
        entries += struct.pack(">32sQQI12x", name.encode("ascii"), offset, len(content), 0)
        data += content
        offset += len(content)
    total = header_size + len(entries) + len(data)
    header = struct.pack(">IIQIII20s16x", 0xDCA24D00, 2, total, len(files), entry_size, 0, b"\0" * 20)
    assert len(header) == header_size
    trp = bytearray(header + entries + data)
    trp[0x1C:0x30] = hashlib.sha1(trp).digest()
    return bytes(trp)


def main():
    trophies = TROPHIES
    if "--no-platinum" in sys.argv:
        trophies = [t for t in TROPHIES if t[1] != "P"]
    icons = {grade: trophy_icon(grade) for grade in "BSGP"}
    files = [("TROPCONF.SFM", sfm(trophies, False)), ("TROP.SFM", sfm(trophies, True)), ("ICON0.PNG", title_icon())]
    for trophy_id, grade, _, _ in trophies:
        files.append(("TROP%03d.PNG" % trophy_id, icons[grade]))
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(here, "..", "sce_sys", "trophy", COMM_ID)
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, "TROPHY.TRP")
    trp = build_trp(files)
    with open(out, "wb") as f:
        f.write(trp)
    print("%s: %d trophies, %d bytes" % (os.path.normpath(out), len(trophies), len(trp)))


if __name__ == "__main__":
    main()
