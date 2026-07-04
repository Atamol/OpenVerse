using System.Globalization;
using System.Text;

namespace OpenVerse.Common;

public static class HeaderCodec
{
    public static string Decode(string s)
    {
        if (s is null || s.Length < 4) return s;
        if (!int.TryParse(s.AsSpan(0, 4), NumberStyles.HexNumber, null, out var len) || len * 4 > s.Length - 4) return s;
        var sb = new StringBuilder(len);
        int n = 2;
        foreach (var c in s.AsSpan(4))
        {
            if (n % 4 == 0) sb.Append((char)(c - 10));
            n++;
            if (sb.Length >= len) break;
        }
        return sb.ToString();
    }
}
