namespace Solvelt.Api.Delivery;

public record StopPoint(Guid Id, double Lat, double Lng);

/// <summary>Orders stops into a driving sequence. The built-in implementation is a nearest-neighbour heuristic (straight-line distance); a real routing provider can replace it (docs/ROADMAP.md).</summary>
public interface IRouteOptimizer
{
    Task<IReadOnlyList<Guid>> OrderAsync(double? startLat, double? startLng, IReadOnlyList<StopPoint> stops, CancellationToken ct = default);
}

public class NearestNeighbourOptimizer : IRouteOptimizer
{
    public Task<IReadOnlyList<Guid>> OrderAsync(double? startLat, double? startLng, IReadOnlyList<StopPoint> stops, CancellationToken ct = default)
    {
        var left = stops.ToList();
        var result = new List<Guid>();
        (double Lat, double Lng)? cur = startLat is { } a && startLng is { } b ? (a, b) : null;
        if (cur is null && left.Count > 0) { var f = left[0]; left.RemoveAt(0); result.Add(f.Id); cur = (f.Lat, f.Lng); }
        while (left.Count > 0)
        {
            var next = left.OrderBy(s => Geo.Km(cur!.Value.Lat, cur.Value.Lng, s.Lat, s.Lng)).ThenBy(s => s.Id).First();
            left.Remove(next); result.Add(next.Id); cur = (next.Lat, next.Lng);
        }
        return Task.FromResult<IReadOnlyList<Guid>>(result);
    }
}

public static class Eta
{
    public const double SpeedKmh = 30, ServiceMinutes = 4;
    public static double TravelMinutes(double km) => km / SpeedKmh * 60;

    /// <summary>Rough ETA per remaining stop, in order: legs at 30 km/h in a straight line plus 4 minutes at every earlier stop.</summary>
    public static List<DateTime?> ForStops(double fromLat, double fromLng, IReadOnlyList<(double? Lat, double? Lng)> stops, DateTime now)
    {
        var res = new List<DateTime?>();
        var (lat, lng) = (fromLat, fromLng);
        var t = now;
        foreach (var s in stops)
        {
            if (s.Lat is not { } sl || s.Lng is not { } sg) { res.Add(null); continue; }
            t = t.AddMinutes(TravelMinutes(Geo.Km(lat, lng, sl, sg)));
            res.Add(t);
            t = t.AddMinutes(ServiceMinutes);
            (lat, lng) = (sl, sg);
        }
        return res;
    }
}

/// <summary>Accepts JPEG and PNG only (by magic bytes, never the client's content type) and removes every metadata block that can carry a location (EXIF, XMP, text chunks).</summary>
public static class ImageSanitizer
{
    public static (byte[]? Data, string? ContentType) Clean(byte[] d)
    {
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8) return Jpeg(d) is { } j ? (j, "image/jpeg") : (null, null);
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A)
            return Png(d) is { } p ? (p, "image/png") : (null, null);
        return (null, null);
    }

    private static byte[]? Jpeg(byte[] d)
    {
        var o = new MemoryStream(); o.WriteByte(0xFF); o.WriteByte(0xD8);
        var i = 2;
        while (i + 3 < d.Length)
        {
            if (d[i] != 0xFF) return null;
            var m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xDA) { o.Write(d, i, d.Length - i); return o.ToArray(); }      // start of scan: the rest is image data
            if (m is 0xD8 or 0x01 || m is >= 0xD0 and <= 0xD7) { o.Write(d, i, 2); i += 2; continue; }
            var len = (d[i + 2] << 8) | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) return null;
            if (m is not (0xE1 or 0xED or 0xFE)) o.Write(d, i, 2 + len);              // drop APP1 (EXIF/XMP), APP13 (IPTC) and comments
            i += 2 + len;
        }
        return null;
    }

    private static readonly HashSet<string> DropChunks = ["eXIf", "tEXt", "iTXt", "zTXt", "tIME"];

    private static byte[]? Png(byte[] d)
    {
        var o = new MemoryStream(); o.Write(d, 0, 8);
        var i = 8;
        while (i + 12 <= d.Length)
        {
            var len = (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
            if (len < 0 || i + 12 + len > d.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (!DropChunks.Contains(type)) o.Write(d, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") return o.ToArray();
        }
        return null;
    }
}
