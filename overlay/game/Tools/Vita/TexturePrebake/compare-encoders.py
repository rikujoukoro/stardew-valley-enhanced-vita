"""Record fixed-source old/new error, including the reviewed canopy crop."""
import json
import numpy as np
from PIL import Image
from bc3_encoder import ROOT

out=ROOT/'scratchpad/texture-prebake/encoder-correction-20261027'
old=ROOT/'scratchpad/texture-prebake/summer_outdoorsTileSheet-bc3'
new=out/'summer_outdoorsTileSheet-bc3-stb'
stem='summer_outdoorsTileSheet'
src=np.asarray(Image.open(new/(stem+'-source.png')).convert('RGBA')).astype(np.int16)
results={}
for label,path in [('pillow',old),('stb',new)]:
    dst=np.asarray(Image.open(path/(stem+'-preview.png')).convert('RGBA')).astype(np.int16)
    err=np.abs(src-dst)
    visible=src[:,:,3]>0
    canopy_src,canopy_dst=src[:96,:96],dst[:96,:96]
    green=(canopy_src[:,:,1]>canopy_src[:,:,0]+40)&(canopy_src[:,:,3]>200)
    results[label]={
        'rgbMae':round(float(err[:,:,:3].mean()),4),
        'visibleRgbMae':round(float(err[:,:,:3][visible].mean()),4),
        'alphaMae':round(float(err[:,:,3].mean()),4),
        'canopyRgbMae':round(float(err[:96,:96,:3].mean()),4),
        'canopyGreenPixelsTurnedRedDominant':int((green&(canopy_dst[:,:,0]>=canopy_dst[:,:,1])).sum()),
        'reviewedPixel':dst[31,68].tolist(),
    }
(out/'quality-comparison.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print(json.dumps(results,indent=2))
