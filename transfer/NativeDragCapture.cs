using System;
using System.Globalization;
using System.Text;

namespace MwbTransfer
{
    // Preserve the existing SendDragFile(string) IPC signature. This envelope is
    // explicitly versioned; legacy filenames are never interpreted as a reply.
    internal static class NativeDragCapture
    {
        const string Prefix="MWB-NATIVE-DRAG/1|";
        const int MaxPathCharacters=32767;
        static readonly Encoding Utf8=new UTF8Encoding(false,true);

        internal static string Encode(Guid leg, int count, string path)
        {
            if(leg==Guid.Empty || count<1 || count>65535 || path==null ||
                path.Length==0 || path.Length>MaxPathCharacters || path.IndexOf('\0')>=0)
                throw new ArgumentException("Invalid capture reply");
            return Prefix+leg.ToString("N")+"|"+count.ToString(CultureInfo.InvariantCulture)+"|"+
                Convert.ToBase64String(Utf8.GetBytes(path));
        }

        internal static bool TryDecode(string text, out Guid leg, out int count, out string path)
        {
            leg=Guid.Empty; count=0; path=null;
            // Bound allocation before Split/Base64/UTF-8 parsing on the IPC boundary.
            if(text==null || text.Length>Prefix.Length+32+8+4*MaxPathCharacters ||
                !text.StartsWith(Prefix,StringComparison.Ordinal)) return false;
            string[] fields=text.Substring(Prefix.Length).Split('|');
            Guid parsedLeg; int parsedCount;
            if(fields.Length!=3 || fields[0].Length!=32 ||
                !Guid.TryParseExact(fields[0],"N",out parsedLeg) || parsedLeg==Guid.Empty ||
                !Int32.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out parsedCount) ||
                parsedCount<1 || parsedCount>65535) return false;
            try
            {
                string parsedPath=Utf8.GetString(Convert.FromBase64String(fields[2]));
                if(parsedPath.Length==0 || parsedPath.Length>MaxPathCharacters || parsedPath.IndexOf('\0')>=0) return false;
                leg=parsedLeg; count=parsedCount; path=parsedPath; return true;
            }
            catch(FormatException) { return false; }
            catch(DecoderFallbackException) { return false; }
        }
    }
}
