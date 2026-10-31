"""BC3 layout/alpha/565 regression fixtures, independently decoded against Pillow."""
from pathlib import Path
import json, struct
import numpy as np
from PIL import Image
from bc3_encoder import ROOT, encode

OUT = ROOT / 'scratchpad/texture-prebake/encoder-correction-20261027'

def decode_reference(data, width, height):
    result = np.empty((height,width,4),dtype=np.uint8)
    def rgb565(word):
        r,g,b=(word>>11)&31,(word>>5)&63,word&31
        return np.array([(r<<3)|(r>>2),(g<<2)|(g>>4),(b<<3)|(b>>2)],dtype=np.int32)
    for index in range(width*height//16):
        block=data[index*16:index*16+16]
        a,b=block[:2]
        if a>b:
            alpha=[a,b]+[((7-i)*a+i*b)//7 for i in range(1,7)]
        else:
            alpha=[a,b]+[((5-i)*a+i*b)//5 for i in range(1,5)]+[0,255]
        c0,c1,bits=struct.unpack('<HHI',block[8:])
        c0,c1=rgb565(c0),rgb565(c1)
        colors=[c0,c1,(2*c0+c1)//3,(c0+2*c1)//3]
        abits=int.from_bytes(block[2:8],'little')
        y,x=divmod(index,width//4)
        for p in range(16):
            result[y*4+p//4,x*4+p%4,:3]=colors[(bits>>(p*2))&3]
            result[y*4+p//4,x*4+p%4,3]=alpha[(abits>>(p*3))&7]
    return result

def fixture(name, array):
    image=Image.fromarray(array)
    path=OUT/(name+'.dds')
    coded=encode(image,path)
    with Image.open(path) as decoded:
        actual=np.asarray(decoded.convert('RGBA')).copy()
    reference=decode_reference(coded,image.width,image.height)
    diff=np.abs(reference.astype(np.int16)-actual.astype(np.int16))
    assert diff.max() <= 1, (name,'independent decoder mismatch',int(diff.max()))
    image.save(OUT/(name+'-source.png'))
    Image.fromarray(actual).save(OUT/(name+'-decoded.png'))
    return actual

colors=[(255,0,0,255),(0,255,0,255),(0,0,255,255),(63,127,31,255),(255,255,255,255),(0,0,0,0),(32,64,96,127),(0,0,0,1)]
tiles=np.zeros((8,16,4),dtype=np.uint8)
for n,color in enumerate(colors):
    y,x=divmod(n,4)
    tiles[y*4:y*4+4,x*4:x*4+4]=color
actual=fixture('solid-rowstride-rgba',tiles)
error=np.abs(tiles.astype(np.int16)-actual.astype(np.int16))
assert error[:,:,:3].max()<=4
assert error[:,:,3].max()==0
alphas=np.array([0,1,16,32,48,64,80,96,112,128,144,160,192,224,254,255],dtype=np.uint8).reshape(4,4)
alpha_image=np.zeros((4,4,4),dtype=np.uint8)
alpha_image[:,:,3]=alphas
gradient=fixture('alpha-gradient',alpha_image)
assert np.abs(gradient[:,:,3].astype(np.int16)-alphas).max()<=18
source=Image.open(ROOT/'scratchpad/texture-prebake/summer_outdoorsTileSheet-bc3/summer_outdoorsTileSheet-source.png').convert('RGBA')
canopy=np.asarray(source.crop((0,0,96,96))).copy()
canopy_decoded=fixture('canopy',canopy)
# Pillow's old output was [255,231,0,253]: the green/cyan leaf became yellow.
# BC3 remains lossy; require the corrected pixel's green dominance and bounded error.
assert canopy_decoded[31,68,1] > canopy_decoded[31,68,0]
assert max(abs(canopy_decoded[31,68].astype(int)-canopy[31,68].astype(int)))<=64
results={'solidRgbMax':int(error[:,:,:3].max()),'solidAlphaMax':int(error[:,:,3].max()),'gradientAlphaMax':int(np.abs(gradient[:,:,3].astype(np.int16)-alphas).max()),'canopyProblemPixel':{'source':canopy[31,68].tolist(),'decoded':canopy_decoded[31,68].tolist()},'independentDecoderMaxDifferenceAllowed':1,'tests':'PASS'}
(OUT/'encoder-fixtures.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print(json.dumps(results,indent=2))
