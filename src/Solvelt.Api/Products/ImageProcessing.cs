using System.Buffers.Binary;

namespace Solvelt.Api.Products;

/// <summary>
/// Image validation and metadata stripping WITHOUT a third-party imaging library (decision: the common .NET libraries are either
/// commercially licensed or heavyweight; a small container-level rewriter is enough to drop EXIF/XMP/IPTC/text chunks).
/// Pixel data is never re-encoded. Also reads the pixel size from the header.
/// </summary>
public static class ImageProcessing
{
    public record Result(string ContentType, byte[] Data, int Width, int Height);

    public static string? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return "image/png";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return "image/webp";
        return null;
    }

    public static string Extension(string contentType) => contentType switch { "image/jpeg" => ".jpg", "image/png" => ".png", _ => ".webp" };

    /// <summary>Returns null when the bytes are not a well-formed image of the sniffed type.</summary>
    public static Result? Process(byte[] data)
    {
        var type = Sniff(data);
        try
        {
            return type switch
            {
                "image/jpeg" => Jpeg(data),
                "image/png" => Png(data),
                "image/webp" => WebP(data),
                _ => null
            };
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException) { return null; }
    }

    private static Result? Jpeg(byte[] d)
    {
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 2);
        var i = 2; int w = 0, h = 0;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return null;
            var m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xD9) { o.Write(d, i, 2); break; }
            if (m is 0x01 or (>= 0xD0 and <= 0xD7)) { o.Write(d, i, 2); i += 2; continue; }
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            if (len < 2 || i + 2 + len > d.Length) return null;
            if (m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && len >= 8)
            { h = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5)); w = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 7)); }
            var drop = m is 0xE1 or 0xED or 0xFE || (m >= 0xE3 && m <= 0xEF); // keep APP0 (JFIF) and APP2 (ICC); drop EXIF/XMP, Photoshop/IPTC, comments, other APPn
            if (!drop) o.Write(d, i, 2 + len);
            i += 2 + len;
            if (m == 0xDA) { o.Write(d, i, d.Length - i); i = d.Length; break; } // entropy-coded data until EOF
        }
        return w > 0 && h > 0 ? new("image/jpeg", o.ToArray(), w, h) : null;
    }

    private static readonly HashSet<string> DropPng = ["eXIf", "tEXt", "zTXt", "iTXt", "tIME"];

    private static Result? Png(byte[] d)
    {
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 8);
        var i = 8; int w = 0, h = 0; var first = true; var sawEnd = false;
        while (i + 12 <= d.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
            if (len < 0 || i + 12 + len > d.Length) return null;
            var name = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (first) { if (name != "IHDR" || len != 13) return null; w = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i + 8)); h = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i + 12)); first = false; }
            if (!DropPng.Contains(name)) o.Write(d, i, 12 + len);
            i += 12 + len;
            if (name == "IEND") { sawEnd = true; break; }
        }
        return sawEnd && w > 0 && h > 0 ? new("image/png", o.ToArray(), w, h) : null;
    }

    private static Result? WebP(byte[] d)
    {
        var riffSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        if (riffSize + 8 > d.Length || riffSize < 12) return null;
        using var body = new MemoryStream();
        var i = 12; int w = 0, h = 0;
        var end = riffSize + 8;
        while (i + 8 <= end)
        {
            var tag = System.Text.Encoding.ASCII.GetString(d, i, 4);
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 4));
            var padded = len + (len & 1);
            if (len < 0 || i + 8 + padded > end + 1 || i + 8 + len > d.Length) return null;
            var chunk = d.AsSpan(i, 8 + Math.Min(padded, d.Length - i - 8)).ToArray();
            switch (tag)
            {
                case "VP8X" when len >= 10:
                    chunk[8] = (byte)(chunk[8] & ~0x0C); // clear EXIF + XMP flags
                    w = 1 + (chunk[12] | chunk[13] << 8 | chunk[14] << 16); h = 1 + (chunk[15] | chunk[16] << 8 | chunk[17] << 16);
                    body.Write(chunk); break;
                case "VP8 " when len >= 10 && w == 0:
                    if (d[i + 11] == 0x9D && d[i + 12] == 0x01 && d[i + 13] == 0x2A) { w = (d[i + 14] | d[i + 15] << 8) & 0x3FFF; h = (d[i + 16] | d[i + 17] << 8) & 0x3FFF; }
                    body.Write(chunk); break;
                case "VP8L" when len >= 5 && w == 0:
                    { var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 9)); w = (int)(bits & 0x3FFF) + 1; h = (int)((bits >> 14) & 0x3FFF) + 1; }
                    body.Write(chunk); break;
                case "EXIF" or "XMP ": break;
                default: body.Write(chunk); break;
            }
            i += 8 + padded;
        }
        if (w <= 0 || h <= 0) return null;
        var o = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(o); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), (uint)(4 + body.Length)); "WEBP"u8.CopyTo(o.AsSpan(8));
        body.ToArray().CopyTo(o, 12);
        return new("image/webp", o, w, h);
    }
}
