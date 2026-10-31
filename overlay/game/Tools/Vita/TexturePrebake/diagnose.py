"""Small, deterministic Pillow BC3 diagnostics; writes only scratchpad evidence."""
import io, json, struct
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[4]
OUT = ROOT / 'scratchpad/texture-prebake/encoder-correction-20261027'
OUT.mkdir(exist_ok=True)

def roundtrip(image):
    stream = io.BytesIO()
    image.save(stream, format='DDS', pixel_format='DXT5')
    stream.seek(0)
    return np.asarray(Image.open(stream).convert('RGBA')).astype(np.int16)

results = {}
for rgb in [(255,0,0),(0,255,0),(0,0,255),(63,127,31),(255,255,255)]:
    image = Image.new('RGBA',(4,4),(*rgb,255))
    results[str(rgb)] = roundtrip(image)[0,0].tolist()
for alpha in [0,1,32,64,127,128,192,254,255]:
    image = Image.new('RGBA',(4,4),(0,0,0,alpha))
    results['alpha='+str(alpha)] = roundtrip(image)[0,0].tolist()
src = np.asarray(Image.open(ROOT / 'scratchpad/texture-prebake/summer_outdoorsTileSheet-bc3/summer_outdoorsTileSheet-source.png')).astype(np.int16)
dst = roundtrip(Image.fromarray(src.astype(np.uint8),'RGBA'))
err = np.abs(src-dst)
visible = src[:,:,3] > 0
results['summer'] = {'rgbMae':float(err[:,:,:3].mean()),'visibleRgbMae':float(err[:,:,:3][visible].mean()),'alphaMae':float(err[:,:,3].mean()),'max':int(err.max())}
score = err[:,:,:3].max(axis=2) * visible
for label, part in [('whole',score),('canopy',score[:96,:96])]:
    y,x = np.unravel_index(part.argmax(), part.shape)
    box = (x//4*4,y//4*4,x//4*4+4,y//4*4+4)
    a=src[box[1]:box[3],box[0]:box[2]];b=dst[box[1]:box[3],box[0]:box[2]]
    results[label+'Worst']={'xy':[int(x),int(y)],'source':src[y,x].tolist(),'decoded':dst[y,x].tolist(),'sourceBlock':a.tolist(),'decodedBlock':b.tolist()}
(OUT/'pillow-diagnostics.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in results.items() if not k.endswith('Worst')},indent=2))
print('Worst visible:',results['wholeWorst']['xy'],results['wholeWorst']['source'],results['wholeWorst']['decoded'])
print('Worst canopy:',results['canopyWorst']['xy'],results['canopyWorst']['source'],results['canopyWorst']['decoded'])
