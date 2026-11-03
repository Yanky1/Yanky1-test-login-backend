using Solvelt.Api.Products;

namespace Solvelt.Api.Tests;

public class ProductUnitTests
{
    [Theory]
    [InlineData("036000291452", true, "UPC-A")]
    [InlineData("036000291453", false, "UPC-A")]
    [InlineData("0-36000-29145-2", true, "UPC-A")]
    [InlineData("4006381333931", true, "EAN-13")]
    [InlineData("4006381333932", false, "EAN-13")]
    [InlineData("10012345678902", true, "GTIN-14")]
    [InlineData("10012345678903", false, "GTIN-14")]
    [InlineData("978-0-306-40615-7", true, "ISBN-13")]
    [InlineData("9780306406158", false, "ISBN-13")]
    [InlineData("0306406152", true, "ISBN-10")]
    [InlineData("0306406153", false, null)]
    [InlineData("96385074", true, "EAN-8")]
    [InlineData("12345", false, null)]
    [InlineData("12345678901A", false, null)]
    public void Barcode_check_digits(string input, bool valid, string? type)
    {
        var r = Barcode.Check(input);
        Assert.Equal(valid, r.Valid);
        if (valid) Assert.Equal(type, r.Type); else Assert.NotNull(r.Error);
    }

    [Fact]
    public void Markdown_never_emits_raw_html_or_script_links()
    {
        var html = SafeMarkdown.Render("# Hi <script>alert(1)</script>\n\n**bold** <img src=x onerror=alert(1)> [bad](javascript:alert(1)) [ok](https://example.com/a?b=1&c=2)\n\n- one\n- two");
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<img", html); Assert.DoesNotContain("javascript:", html);
        Assert.Contains("&lt;script&gt;", html); Assert.Contains("<strong>bold</strong>", html); Assert.Contains("<ul><li>one</li><li>two</li></ul>", html);
        Assert.Contains("rel=\"noopener nofollow ugc\"", html); Assert.Contains("href=\"https://example.com/a?b=1&amp;c=2\"", html);
    }

    [Fact]
    public void Png_metadata_is_stripped_and_size_read()
    {
        var r = ImageProcessing.Process(TestImages.Png(1500, 1000))!;
        Assert.Equal((1500, 1000, "image/png"), (r.Width, r.Height, r.ContentType));
        var s = System.Text.Encoding.Latin1.GetString(r.Data);
        Assert.DoesNotContain("SECRET", s); Assert.DoesNotContain("tEXt", s); Assert.Contains("IDAT", s);
    }

    [Fact]
    public void Jpeg_metadata_is_stripped_and_size_read()
    {
        var r = ImageProcessing.Process(TestImages.Jpeg(1300, 900))!;
        Assert.Equal((1300, 900), (r.Width, r.Height));
        var s = System.Text.Encoding.Latin1.GetString(r.Data);
        Assert.DoesNotContain("SECRET", s); Assert.DoesNotContain("secret comment", s); Assert.Contains("JFIF", s);
        Assert.Equal(0xFF, r.Data[^2]); Assert.Equal(0xD9, r.Data[^1]);
    }

    [Fact]
    public void WebP_metadata_is_stripped_and_flags_cleared()
    {
        var r = ImageProcessing.Process(TestImages.WebP(1100, 1100))!;
        Assert.Equal((1100, 1100), (r.Width, r.Height));
        var s = System.Text.Encoding.Latin1.GetString(r.Data);
        Assert.DoesNotContain("EXIF", s); Assert.DoesNotContain("XMP", s);
        Assert.Equal(0, r.Data[20] & 0x0C);
        Assert.Equal(r.Data.Length - 8, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Data.AsSpan(4)));
    }

    [Fact]
    public void Garbage_and_truncated_images_are_rejected()
    {
        Assert.Null(ImageProcessing.Process("hello world, not an image"u8.ToArray()));
        Assert.Null(ImageProcessing.Process(TestImages.Png()[..40]));
        Assert.Null(ImageProcessing.Process([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
    }

    [Fact]
    public void Variants_are_the_cartesian_product_in_option_order()
    {
        var c = VariantGenerator.Combos([["S", "M"], ["Red", "Blue", "Green"]]);
        Assert.Equal(6, c.Count);
        Assert.Equal(["S", "Red"], c[0]); Assert.Equal(["S", "Blue"], c[1]); Assert.Equal(["M", "Green"], c[5]);
        Assert.Empty(VariantGenerator.Combos([]));
        Assert.Equal("red|large", VariantGenerator.Key([" Red", "LARGE "]));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "'=HYPERLINK(\"\"http://evil\"\")")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    public void Csv_cells_are_formula_safe(string input, string expected)
    {
        var cell = CsvFormat.Cell(input);
        Assert.Equal(input.StartsWith('=') ? "\"" + expected + "\"" : expected, cell);
    }

    [Fact]
    public void Csv_roundtrip_and_parser_edge_cases()
    {
        var rows = CsvFormat.Parse("﻿a,b\r\n\"x, \"\"y\"\"\",\"line1\nline2\"\n,\n");
        Assert.Equal(["a", "b"], rows[0]); Assert.Equal(["x, \"y\"", "line1\nline2"], rows[1]);
        Assert.Equal("=1+1", CsvFormat.Unescape("'=1+1")); Assert.Equal("'quoted", CsvFormat.Unescape("'quoted"));
        Assert.Throws<FormatException>(() => CsvFormat.Parse("a,\"b"));
    }

    [Fact]
    public void Evaluate_flags_required_and_recommended_items()
    {
        var p = new Solvelt.Api.Domain.Product { Title = "Mug" };
        var r = ProductRules.Evaluate(p, []);
        Assert.False(r.CanPublish);
        foreach (var k in new[] { "title", "category", "images", "price", "stock" }) Assert.Contains(r.Issues, i => i.Key == k && i.Severity == "required");
        Assert.Contains(r.Issues, i => i.Key == "bullets" && i.Severity == "recommended");
    }
}
