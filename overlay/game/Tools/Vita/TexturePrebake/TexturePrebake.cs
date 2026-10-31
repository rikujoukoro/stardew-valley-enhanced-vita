using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework.Content;

class TexturePrebake
{
    static int Read7(BinaryReader r) { int v=0,s=0; while (true) { byte b=r.ReadByte(); v|=(b&127)<<s; if ((b&128)==0) return v; s+=7; if(s>28) throw new InvalidDataException("bad 7bit"); } }
    static string ReadString(BinaryReader r) { int n=Read7(r); return Encoding.UTF8.GetString(r.ReadBytes(n)); }
    static byte[] Expand(byte[] input)
    {
        int expected=BitConverter.ToInt32(input,10);
        int packedLen=input.Length-14;
        byte[] packed=new byte[packedLen]; Buffer.BlockCopy(input,14,packed,0,packedLen);
        Type t=typeof(ContentManager).Assembly.GetType("Microsoft.Xna.Framework.Content.LzxDecoder",true);
        object decoder=Activator.CreateInstance(t,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{16},null);
        MethodInfo decode=t.GetMethod("Decompress",BindingFlags.Instance|BindingFlags.Public);
        MemoryStream src=new MemoryStream(packed), dst=new MemoryStream(expected); long pos=0;
        while(pos<packed.Length)
        {
            src.Position=pos; int hi=src.ReadByte(),lo=src.ReadByte(); if(hi<0||lo<0) throw new EndOfStreamException();
            int block=(hi<<8)|lo, frame=32768;
            if(hi==255) { hi=lo; lo=src.ReadByte(); frame=(hi<<8)|lo; hi=src.ReadByte(); lo=src.ReadByte(); block=(hi<<8)|lo; pos+=5; }
            else pos+=2;
            if(block==0||frame==0) break;
            decode.Invoke(decoder,new object[]{src,block,dst,frame}); pos+=block;
        }
        if(dst.Length!=expected) throw new InvalidDataException("LZX output size mismatch: "+dst.Length+" vs "+expected);
        return dst.ToArray();
    }
    static void Main(string[] args)
    {
        if(args.Length!=2) throw new ArgumentException("input.xnb output-prefix");
        byte[] x=File.ReadAllBytes(args[0]); byte[] body=(x[5]&128)!=0?Expand(x):Slice(x,10,x.Length-10);
        using(MemoryStream ms=new MemoryStream(body)) using(BinaryReader r=new BinaryReader(ms))
        {
            int readerCount=Read7(r); string[] names=new string[readerCount];
            for(int i=0;i<readerCount;i++){ names[i]=ReadString(r); r.ReadInt32(); }
            int shared=Read7(r); int root=Read7(r);
            if(shared!=0) throw new InvalidDataException("pilot only accepts zero shared resources; found "+shared);
            if(root<1||root>readerCount||!names[root-1].Contains("Texture2DReader")) throw new InvalidDataException("root asset not Texture2D");
            int texStart=(int)ms.Position; int fmt=r.ReadInt32(),w=r.ReadInt32(),h=r.ReadInt32(),levels=r.ReadInt32();
            int n=r.ReadInt32(); byte[] level0=r.ReadBytes(n); if(level0.Length!=n)throw new EndOfStreamException();
            if(ms.Position!=body.Length)throw new InvalidDataException("unexpected trailing content bytes: "+(body.Length-ms.Position));
            if(fmt!=0 && fmt!=6)throw new InvalidDataException("unexpected format "+fmt);
            if(levels!=1)throw new InvalidDataException("pilot expects one mip level; found "+levels);
            File.WriteAllBytes(args[1]+(fmt==0?".rgba":".level0.bin"),level0);
            File.WriteAllText(args[1]+".json",string.Format("{{\"format\":{0},\"width\":{1},\"height\":{2},\"mipLevels\":{3},\"level0Bytes\":{4},\"decodedBytes\":{5},\"sharedResources\":{6},\"textureBodyOffset\":{7},\"textureBodyEnd\":{8}}}",fmt,w,h,levels,n,body.Length,shared,texStart,(int)ms.Position));
            File.WriteAllBytes(args[1]+".body",body);
        }
    }
    static byte[] Slice(byte[] b,int o,int n){byte[] x=new byte[n];Buffer.BlockCopy(b,o,x,0,n);return x;}
}




