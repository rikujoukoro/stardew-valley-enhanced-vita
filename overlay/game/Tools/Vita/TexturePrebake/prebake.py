from __future__ import annotations
import hashlib, json, struct, subprocess, sys
from pathlib import Path
from PIL import Image
from bc3_encoder import encode, STB_SHA256

ROOT = Path(__file__).resolve().parents[4]
HELPER = ROOT / 'scratchpad/texture-prebake/TexturePrebake.exe'
SOURCE_ROOT = ROOT / 'vita-install/StardewValley/Content/Maps'
ALLOW = {
    'spring_outdoorsTileSheet.xnb': '170b3c3d4adb8f0b2954b86caca9611a115d8232ac795fe0752c62381bf795b9',
    'summer_outdoorsTileSheet.xnb': 'c78a286270c48c972bb6d094c902e98111c27a03168e58d4a5c1a9ea16d82d18',
}

def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def main() -> None:
    if len(sys.argv) != 2 or sys.argv[1] not in ALLOW:
        raise SystemExit('Usage: prebake.py spring_outdoorsTileSheet.xnb | summer_outdoorsTileSheet.xnb')
    name = sys.argv[1]
    source = SOURCE_ROOT / name
    original = source.read_bytes()
    source_hash = sha256(original)
    if source_hash != ALLOW[name]:
        raise SystemExit(f'Whitelist SHA mismatch for {name}: {source_hash}')
    OUT = ROOT / 'scratchpad/texture-prebake/encoder-correction-20261027' / (source.stem + '-bc3-stb')
    OUT.mkdir(parents=True, exist_ok=True)
    prefix = OUT / source.stem
    subprocess.run([str(HELPER), str(source), str(prefix)], check=True)
    meta = json.loads(prefix.with_suffix('.json').read_text(encoding='utf-8'))
    body = prefix.with_suffix('.body').read_bytes()
    if meta['sharedResources'] != 0:
        raise SystemExit(f"Unexpected shared resource count: {meta['sharedResources']}")
    if meta['textureBodyEnd'] != len(body):
        raise SystemExit('Unexpected trailing XNB content bytes')
    width, height = meta['width'], meta['height']
    if width % 4 or height % 4:
        raise SystemExit(f'DXT requires dimensions multiple of 4; got {width}x{height}')
    rgba = prefix.with_suffix('.rgba').read_bytes()
    if len(rgba) != width * height * 4:
        raise SystemExit(f'Unexpected RGBA length: {len(rgba)}')
    image = Image.frombytes('RGBA', (width, height), rgba)
    source_png = prefix.with_name(prefix.name + '-source.png')
    image.save(source_png)
    dds_path = prefix.with_suffix('.dds')
    encode(image, dds_path)
    dds = dds_path.read_bytes()
    if dds[:4] != b'DDS ' or dds[84:88] != b'DXT5':
        raise SystemExit('Encoder did not produce a DXT5 DDS')
    coded = dds[128:]
    expected = (width // 4) * (height // 4) * 16
    if len(coded) != expected:
        raise SystemExit(f'Unexpected BC3 size: {len(coded)} != {expected}')
    # Store a view of the compressed result and a decoded PNG for visual review.
    with Image.open(dds_path) as decoded:
        decoded_rgba = decoded.convert('RGBA')
        decoded_rgba.save(prefix.with_name(prefix.name + '-preview.png'))
    import numpy as np
    original_pixels = np.asarray(image, dtype=np.int16)
    decoded_pixels = np.asarray(decoded_rgba, dtype=np.int16)
    error = np.abs(original_pixels - decoded_pixels)
    metrics = {
        'alphaMae': round(float(error[:, :, 3].mean()), 4),
        'alphaMax': int(error[:, :, 3].max()),
        'premultipliedRgbMae': round(float(error[:, :, :3].mean()), 4),
        'premultipliedRgbMax': int(error[:, :, :3].max()),
    }
    crops = [(0, 0, 96, 96), (max(0, width // 2 - 48), max(0, height // 2 - 48), min(width, width // 2 + 48), min(height, height // 2 + 48)), (max(0, width - 96), max(0, height - 96), width, height)]
    crop_files = []
    for index, box in enumerate(crops, start=1):
        left, top, right, bottom = box
        if right - left < 4 or bottom - top < 4:
            continue
        src_crop = image.crop(box).resize(((right-left)*3, (bottom-top)*3), Image.Resampling.NEAREST)
        dst_crop = decoded_rgba.crop(box).resize(((right-left)*3, (bottom-top)*3), Image.Resampling.NEAREST)
        comparison = Image.new('RGBA', (src_crop.width + dst_crop.width, src_crop.height + 18), (24, 24, 24, 255))
        comparison.paste(src_crop, (0, 18))
        comparison.paste(dst_crop, (src_crop.width, 18))
        from PIL import ImageDraw
        draw = ImageDraw.Draw(comparison)
        draw.text((2, 2), 'source', fill=(255, 255, 255, 255))
        draw.text((src_crop.width + 2, 2), 'BC3', fill=(255, 255, 255, 255))
        crop_path = prefix.with_name(prefix.name + f'-crop-{index}.png')
        comparison.save(crop_path)
        crop_files.append(crop_path.name)
    texture_offset = meta['textureBodyOffset']
    block = struct.pack('<4i', 6, width, height, 1) + struct.pack('<i', len(coded)) + coded
    new_body = body[:texture_offset] + block + body[meta['textureBodyEnd']:]
    xnb = b'XNBw' + bytes((5, 0)) + struct.pack('<I', 10 + len(new_body)) + new_body
    xnb_path = prefix.with_name(prefix.name + '-bc3.xnb')
    xnb_path.write_bytes(xnb)
    verify_prefix = OUT / (source.stem + '-verify')
    subprocess.run([str(HELPER), str(xnb_path), str(verify_prefix)], check=True)
    verified = json.loads(verify_prefix.with_suffix('.json').read_text(encoding='utf-8'))
    verified_payload = verify_prefix.with_suffix('.level0.bin').read_bytes()
    if (verified['format'], verified['width'], verified['height'], verified['mipLevels'], len(verified_payload)) != (6, width, height, 1, len(coded)):
        raise SystemExit('FNA helper round-trip validation failed')
    manifest = {
        'source': str(source.relative_to(ROOT)).replace('\\', '/'),
        'sourceBytes': len(original), 'sourceSha256': source_hash,
        'outputXnb': xnb_path.name, 'outputBytes': len(xnb), 'outputSha256': sha256(xnb),
        'surfaceFormat': 'Dxt5 / BC3', 'width': width, 'height': height, 'mipLevels': 1,
        'sourceRawBytes': len(rgba), 'codedBytes': len(coded),
        'rawTextureReductionPercent': round(100 * (1 - len(coded) / len(rgba)), 2),
        'premultipliedAlpha': True, 'pillowVersion': Image.__version__,
        'encoder': 'stb_dxt v1.12 STB_DXT_HIGHQUAL (no dithering)',
        'encoderSourceSha256': STB_SHA256,
        'errorMetrics': metrics, 'sourcePng': source_png.name, 'cropComparisons': crop_files,
        'fnaRoundTrip': {'surfaceFormat': verified['format'], 'width': verified['width'], 'height': verified['height'], 'mipLevels': verified['mipLevels'], 'codedBytes': len(verified_payload)},
        'notes': 'Offline pilot only. Output is isolated in scratchpad; no active content updated. Visual and Vita validation required.'
    }
    (OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    for temporary in (prefix.with_suffix('.rgba'), prefix.with_suffix('.level0.bin'), prefix.with_suffix('.body'), prefix.with_suffix('.json'), verify_prefix.with_suffix('.level0.bin'), verify_prefix.with_suffix('.body'), verify_prefix.with_suffix('.json')):
        temporary.unlink(missing_ok=True)
    print(json.dumps(manifest, indent=2))

if __name__ == '__main__':
    main()
