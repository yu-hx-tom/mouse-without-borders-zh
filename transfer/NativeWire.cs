using System;
using System.Security.Cryptography;

namespace MwbTransfer
{
    internal static class NativeWire
    {
        // Existing small packets have exactly 16 payload bytes. Use a 96-bit
        // random nonce plus a uint routing argument; the in-memory Guid is the
        // canonical nonce followed by four zero bytes, everywhere including files.
        internal static Guid NewId()
        {
            byte[] bytes=new byte[16];
            using(RandomNumberGenerator random=RandomNumberGenerator.Create())random.GetBytes(bytes);
            Array.Clear(bytes,12,4);return new Guid(bytes);
        }
        internal static bool Canonical(Guid id)
        {
            byte[] bytes=id.ToByteArray();return id!=Guid.Empty&&BitConverter.ToUInt32(bytes,12)==0;
        }
        internal static byte[] Encode(int type,uint source,uint destination,Guid id,uint argument)
        {
            if(type<80||type>95||!Canonical(id)||source<=1||destination<=1||source==255||destination==255)throw new ArgumentException("Invalid native drag packet");
            // DATA(byte[]) accepts 64 backing bytes; IsBigPackage remains FALSE,
            // so the original encrypted socket writer sends exactly the first 32.
            byte[] bytes=new byte[64];Put(bytes,0,(uint)type);Put(bytes,8,source);Put(bytes,12,destination);
            Array.Copy(id.ToByteArray(),0,bytes,16,12);Put(bytes,28,argument);return bytes;
        }
        internal static bool Decode(byte[] bytes,uint authenticatedPeer,uint local,out int type,out Guid id,out uint argument)
        {
            type=0;id=Guid.Empty;argument=0;
            if(bytes==null||bytes.Length<32)return false;
            int kind=BitConverter.ToInt32(bytes,0);
            if(kind<80||kind>95||BitConverter.ToUInt32(bytes,8)!=authenticatedPeer||BitConverter.ToUInt32(bytes,12)!=local||authenticatedPeer<=1||authenticatedPeer==255)return false;
            byte[] nonce=new byte[16];Array.Copy(bytes,16,nonce,0,12);Guid value=new Guid(nonce);
            if(!Canonical(value))return false;
            type=kind;id=value;argument=BitConverter.ToUInt32(bytes,28);return true;
        }
        static void Put(byte[] bytes,int offset,uint value){Array.Copy(BitConverter.GetBytes(value),0,bytes,offset,4);}
    }
}
