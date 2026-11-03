using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Api.Tests;

/// <summary>Synthetic images (no imaging library): structurally valid containers carrying fake EXIF/text metadata.</summary>
public static class TestImages
{
    public static byte[] Png(int w = 1200, int h = 1200, bool withMetadata = true)
    {
        using var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string name, byte[] data)
        {
            var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); o.Write(len);
            var nb = Encoding.ASCII.GetBytes(name); o.Write(nb); o.Write(data);
            o.Write(new byte[4]); // CRC is not verified by the processor
        }
        var ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h); ihdr[8] = 8; ihdr[9] = 0;
        Chunk("IHDR", ihdr);
        if (withMetadata) { Chunk("tEXt", Encoding.ASCII.GetBytes("Author\0SECRET-GPS-AUTHOR")); Chunk("eXIf", Encoding.ASCII.GetBytes("EXIFSECRET")); }
        using var z = new MemoryStream(); using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(new byte[16]);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        return o.ToArray();
    }

    public static byte[] Jpeg(int w = 1200, int h = 1200, bool withMetadata = true)
    {
        using var o = new MemoryStream();
        o.Write([0xFF, 0xD8]);
        void Seg(byte m, byte[] d) { o.WriteByte(0xFF); o.WriteByte(m); var l = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(l, (ushort)(d.Length + 2)); o.Write(l); o.Write(d); }
        Seg(0xE0, "JFIF\0\x01\x01\0\0\x01\0\x01\0\0"u8.ToArray());
        if (withMetadata) { Seg(0xE1, Encoding.ASCII.GetBytes("Exif\0\0SECRET-GPS")); Seg(0xFE, Encoding.ASCII.GetBytes("secret comment")); }
        var sof = new byte[15]; sof[0] = 8; BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)h); BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)w); sof[5] = 3;
        Seg(0xC0, sof);
        Seg(0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0]);
        o.Write([0x12, 0x34, 0x56]); o.Write([0xFF, 0xD9]);
        return o.ToArray();
    }

    public static byte[] WebP(int w = 1200, int h = 1200, bool withMetadata = true)
    {
        using var body = new MemoryStream();
        void Chunk(string tag, byte[] d)
        {
            body.Write(Encoding.ASCII.GetBytes(tag)); var l = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(l, (uint)d.Length); body.Write(l); body.Write(d); if ((d.Length & 1) == 1) body.WriteByte(0);
        }
        var x = new byte[10]; x[0] = withMetadata ? (byte)0x0C : (byte)0; x[4] = (byte)((w - 1) & 0xFF); x[5] = (byte)(((w - 1) >> 8) & 0xFF); x[7] = (byte)((h - 1) & 0xFF); x[8] = (byte)(((h - 1) >> 8) & 0xFF);
        Chunk("VP8X", x);
        var l8 = new byte[5]; l8[0] = 0x2F; BinaryPrimitives.WriteUInt32LittleEndian(l8.AsSpan(1), (uint)((w - 1) | ((h - 1) << 14)));
        Chunk("VP8L", l8);
        if (withMetadata) { Chunk("EXIF", Encoding.ASCII.GetBytes("EXIFSECRET")); Chunk("XMP ", Encoding.ASCII.GetBytes("XMPSECRET")); }
        var o = new byte[12 + body.Length]; "RIFF"u8.CopyTo(o); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), (uint)(4 + body.Length)); "WEBP"u8.CopyTo(o.AsSpan(8)); body.ToArray().CopyTo(o, 12);
        return o;
    }
}

public static class ProdApi
{
    public static async Task<(Api api, Guid seller)> Approved(ApiFactory f, bool approve = true)
    {
        var (api, _, seller) = await TeamHelpers.NewOwner(f);
        if (!approve) return (api, seller);
        await api.CompleteApplicationAndSubmit();
        var admin = new Api(f); await admin.LoginAs(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, await admin.Post($"/api/v1/admin/sellers/{seller}/status", new { status = "Approved" }).Code());
        return (api, seller);
    }

    public static async Task<JsonNode> Node(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    public static async Task<Guid> CategoryId(Api api, string slug) =>
        (await api.JsonBody(await api.Get("/api/v1/categories"))).EnumerateArray().First(c => c.GetProperty("slug").GetString() == slug).GetProperty("id").GetGuid();

    public static async Task<JsonNode> Create(Api api, string title = "Speckled stoneware coffee mug", string? slug = "kitchen-dining", Guid? brandId = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["categoryId"] = slug is null ? null : await CategoryId(api, slug), ["brandId"] = brandId };
        var r = await api.Post("/api/v1/seller/products", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Node(r);
    }

    public static async Task<JsonNode> Get(Api api, Guid id) { var r = await api.Get($"/api/v1/seller/products/{id}"); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await Node(r); }

    /// <summary>GET the document, let the test tweak it, PUT it back (the detail shape is a superset of the write shape).</summary>
    public static async Task<HttpResponseMessage> Put(Api api, Guid id, Action<JsonObject>? tweak = null)
    {
        var cur = (JsonObject)await Get(api, id);
        tweak?.Invoke(cur);
        return await api.Put($"/api/v1/seller/products/{id}", JsonSerializer.Deserialize<JsonElement>(cur.ToJsonString()));
    }

    public static async Task<HttpResponseMessage> Upload(Api api, Guid id, byte[] data, string fileName = "photo.png", string? alt = "A front view", Guid? variantId = null)
    {
        using var form = new MultipartFormDataContent();
        var fc = new ByteArrayContent(data); fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fc, "file", fileName);
        var url = $"/api/v1/seller/products/{id}/images?alt={Uri.EscapeDataString(alt ?? "")}" + (variantId is null ? "" : $"&variantId={variantId}");
        return await api.Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = form });
    }

    public static async Task<HttpResponseMessage> UploadCsv(Api api, string url, string csv, string name = "products.csv")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", name);
        return await api.Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = form });
    }

    /// <summary>Makes a draft satisfy every publish rule (price, stock, one photo) and returns its id.</summary>
    public static async Task<Guid> ReadyProduct(Api api, string title = "Speckled stoneware coffee mug", Guid? brandId = null, string? sku = null)
    {
        var id = Guid.Parse((string)(await Create(api, title, brandId: brandId))["id"]!);
        Assert.Equal(HttpStatusCode.Created, (await Upload(api, id, TestImages.Png())).StatusCode);
        var r = await Put(api, id, d => { d["price"] = 19.99; d["quantity"] = 10; d["sku"] = sku; d["noBarcode"] = true; });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return id;
    }
}
