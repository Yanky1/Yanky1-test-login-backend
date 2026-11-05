using System.IO.Compression;

namespace Solvelt.Api.Demo;

/// <summary>Placeholder product images for the demo data: small flat-colour PNG tiles generated here (no external download, no licence question).</summary>
public static class DemoAssets
{
    public const int Side = 1000;

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    private static uint Crc(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
    private static void Chunk(Stream o, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); o.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); o.Write(t); o.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc(t, data)); o.Write(len);
    }

    /// <summary>A 1000 x 1000 RGB tile: a soft background, an inset panel and a circle. <paramref name="hue"/> picks the colour family (0-359).</summary>
    public static byte[] Tile(int hue, int variant = 0)
    {
        var bg = Hsl(hue, 0.35, 0.90); var panel = Hsl(hue, 0.40, 0.80); var disc = Hsl((hue + 25 * (variant + 1)) % 360, 0.55, 0.45); var accent = Hsl(hue, 0.30, 0.30);
        var raw = new byte[Side * (1 + Side * 3)];
        int cx = Side / 2, cy = Side / 2 - 30, r = 210 + variant * 40;
        for (var y = 0; y < Side; y++)
        {
            var row = y * (1 + Side * 3); raw[row] = 0;
            for (var x = 0; x < Side; x++)
            {
                (byte R, byte G, byte B) c = bg;
                if (x is >= 120 and < 880 && y is >= 120 and < 880) c = panel;
                var dx = x - cx; var dy = y - cy;
                if (dx * dx + dy * dy < r * r) c = disc;
                if (y is >= 760 and < 800 && x is >= 260 and < 740) c = accent;
                var i = row + 1 + x * 3; raw[i] = c.R; raw[i + 1] = c.G; raw[i + 2] = c.B;
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, Side); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), Side);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var d = new ZLibStream(z, CompressionLevel.SmallestSize, true)) d.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static (byte, byte, byte) Hsl(int h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s; var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1)); var m = l - c / 2;
        (double r, double g, double b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
