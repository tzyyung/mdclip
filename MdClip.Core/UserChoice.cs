using System.Security.Cryptography;
using System.Text;

namespace MdClip;

// The check value Windows keeps beside a file type's default app (HKCU\...\FileExts\<ext>\UserChoice: ProgId
// and Hash). Windows only honours a default whose Hash matches; a program that writes the ProgId alone gets it
// reset. This is the same computation the shell does (as worked out by the community, e.g. SetUserFTA and
// PS-SFTA), not a documented API, so a Windows update may change it.
//   Hash(ext, sid, progId, when): when is the UserChoice key's last write time, cut to the minute.
public static class UserChoice
{
    const string Experience = "User Choice set via Windows User Experience {D18B6DD5-6124-4341-9318-804003BAFA0B}";

    // The key's last write time (UTC FILETIME) as the hash takes it: to the minute, 16 lowercase hex digits.
    public static string Minute(long fileTimeUtc) => (fileTimeUtc - fileTimeUtc % TimeSpan.TicksPerMinute).ToString("x16");

    public static string Hash(string ext, string sid, string progId, long fileTimeUtc)
    {
        var text = (ext + sid + progId + Minute(fileTimeUtc) + Experience).ToLowerInvariant();
        var data = Encoding.Unicode.GetBytes(text + "\0");
        var md5 = MD5.HashData(data);
        // whole 8-byte blocks of the text with its terminator
        var blocks = ((data.Length & 4) == 0 ? 1 : 0) + (data.Length >> 2) - 1;
        if (blocks <= 1) return "";
        var rounds = ((blocks - 2) >> 1) + 1;

        var first = FirstPass(data, rounds, U32(md5, 0) | 1, U32(md5, 4) | 1);
        var second = SecondPass(data, rounds, U32(md5, 0) | 1, U32(md5, 4) | 1);
        var result = new byte[8];
        BitConverter.GetBytes(first.Out1 ^ second.Out1).CopyTo(result, 0);
        BitConverter.GetBytes(first.Out2 ^ second.Out2).CopyTo(result, 4);
        return Convert.ToBase64String(result);
    }

    static uint U32(byte[] bytes, int at) => BitConverter.ToUInt32(bytes, at);

    static (uint Out1, uint Out2) FirstPass(byte[] data, int rounds, uint md51, uint md52)
    {
        unchecked
        {
            md51 += 0x69FB0000;
            md52 += 0x13DB0000;
            uint cache = 0, out1 = 0, out2 = 0;
            for (int i = 0, at = 0; i < rounds; i++, at += 8)
            {
                var r0 = U32(data, at) + out1;
                var r1 = U32(data, at + 4);
                var r2a = r0 * md51 - 0x10FA9605 * (r0 >> 16);
                var r2b = 0x79F8A395 * r2a + 0x689B6B9F * (r2a >> 16);
                var r3 = 0xEA970001 * r2b - 0x3C101569 * (r2b >> 16);
                var r4 = r3 + r1;
                var r5 = cache + r3;
                var r6a = r4 * md52 - 0x3CE8EC25 * (r4 >> 16);
                var r6b = 0x59C3AF2D * r6a - 0x2232E0F1 * (r6a >> 16);
                out1 = 0x1EC90001 * r6b + 0x35BD1EC9 * (r6b >> 16);
                out2 = r5 + out1;
                cache = out2;
            }
            return (out1, out2);
        }
    }

    static (uint Out1, uint Out2) SecondPass(byte[] data, int rounds, uint md51, uint md52)
    {
        unchecked
        {
            uint cache = 0, out1 = 0, out2 = 0;
            for (int i = 0, at = 0; i < rounds; i++, at += 8)
            {
                var r0 = U32(data, at) + out1;
                var r1a = r0 * md51;
                var r1b = 0xB1110000 * r1a - 0x30674EEF * (r1a >> 16);
                var r2a = 0x5B9F0000 * r1b - 0x78F7A461 * (r1b >> 16);
                var r2b = 0x12CEB96D * (r2a >> 16) - 0x46930000 * r2a;
                var r3 = 0x1D830000 * r2b + 0x257E1D83 * (r2b >> 16);
                var r4a = md52 * (r3 + U32(data, at + 4));
                var r4b = 0x16F50000 * r4a - 0x5D8BE90B * (r4a >> 16);
                var r5a = 0x96FF0000 * r4b - 0x2C7C6901 * (r4b >> 16);
                var r5b = 0x2B890000 * r5a + 0x7C932B89 * (r5a >> 16);
                out1 = 0x9F690000 * r5b - 0x405B6097 * (r5b >> 16);
                out2 = out1 + cache + r3;
                cache = out2;
            }
            return (out1, out2);
        }
    }
}
