"""Offline stb_dxt BC3 encoder, with no color-space or alpha conversion."""
from pathlib import Path
import struct, subprocess

ROOT = Path(__file__).resolve().parents[4]
ENCODER = ROOT / 'scratchpad/texture-prebake/encoder-correction-20261027/encode-bc3'
STB_SHA256 = '807667ef98e0fd749cdb65cca0c2d980bc148109d2fed6f1873c81ae0f449933'

def wsl_path(path):
    path = Path(path).resolve()
    return '/mnt/' + path.drive[0].lower() + path.as_posix()[2:]

def encode(image, output):
    if not ENCODER.is_file():
        raise RuntimeError('Build offline encoder first with TexturePrebake/build-encoder.sh in WSL')
    width, height = image.size
    if image.mode != 'RGBA' or width % 4 or height % 4:
        raise ValueError('BC3 input must be RGBA and dimensions divisible by four')
    output = Path(output)
    raw, bc3 = output.with_suffix('.encode.rgba'), output.with_suffix('.encode.bc3')
    raw.write_bytes(image.tobytes())
    try:
        subprocess.run(['wsl.exe', '-u', 'root', '--', wsl_path(ENCODER), str(width), str(height), wsl_path(raw), wsl_path(bc3)], check=True)
        coded = bc3.read_bytes()
        if len(coded) != width * height:
            raise ValueError('Unexpected BC3 payload size')
        # DDS_HEADER + DDS_PIXELFORMAT; one BC3 mip, linear compressed byte count.
        fields = [124, 0x81007, height, width, len(coded), 0, 1] + [0]*11
        header = struct.pack('<18I', *fields) + struct.pack('<II4s5I',32,4,b'DXT5',0,0,0,0,0) + struct.pack('<5I',0x1000,0,0,0,0)
        assert len(header) == 124
        output.write_bytes(b'DDS ' + header + coded)
        return coded
    finally:
        raw.unlink(missing_ok=True)
        bc3.unlink(missing_ok=True)
