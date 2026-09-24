using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using PrintSink.Core.Pdl;

namespace PrintSink.Core.Tests.Pdl;

/// <summary>Tests the PWG Raster decoder used by the clipboard sink.</summary>
[TestClass]
[SuppressMessage("Performance", "CA1812", Justification = "MSTest creates test classes through reflection.")]
internal sealed class PwgRasterReaderTests
{
    /// <summary>Verifies decoding of a one-pixel RGB RaS3 page.</summary>
    [TestMethod]
    public void ReadFirstPageDecodesRgbPixelsToBgra()
    {
        byte[] data = new byte[4 + 1796 + 3];
        data[0] = (byte)'R';
        data[1] = (byte)'a';
        data[2] = (byte)'S';
        data[3] = (byte)'3';
        Span<byte> header = data.AsSpan(4, 1796);
        Write(header, 372, 1);
        Write(header, 376, 1);
        Write(header, 384, 8);
        Write(header, 388, 24);
        Write(header, 392, 3);
        Write(header, 396, 0);
        Write(header, 400, 1);
        Write(header, 404, 0);
        data[^3] = 0x11;
        data[^2] = 0x22;
        data[^1] = 0x33;

        PwgRasterPage page;
        using (MemoryStream stream = new(data))
        {
            page = PwgRasterReader.ReadFirstPage(stream);
        }

        Assert.AreEqual(1, page.Width);
        Assert.AreEqual(1, page.Height);
        CollectionAssert.AreEqual(new byte[] { 0x33, 0x22, 0x11, 0xff }, page.Pixels.ToArray());
    }

    /// <summary>Verifies literal pixels, repeated pixels, and repeated scanlines in RaS2.</summary>
    [TestMethod]
    public void ReadFirstPageDecodesCompressedRows()
    {
        byte[] data = CreateCompressedPage(4, 2, [1, 255, 255, 0, 0, 0, 255, 0, 1, 0, 0, 255]);
        using MemoryStream stream = new(data);
        PwgRasterPage page = PwgRasterReader.ReadFirstPage(stream);
        byte[] row = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255];
        CollectionAssert.AreEqual(row.Concat(row).ToArray(), page.Pixels.ToArray());
    }

    /// <summary>Verifies malformed compressed payloads fail predictably.</summary>
    [TestMethod]
    public void ReadFirstPageRejectsInvalidCompressedRows()
    {
        byte[][] payloads = [[0, 0, 255], [0, 2, 255, 0, 0], [1, 0, 255, 0, 0]];
        foreach (byte[] payload in payloads)
        {
            using MemoryStream stream = new(CreateCompressedPage(1, 1, payload));
            Assert.ThrowsExactly<InvalidDataException>(() => PwgRasterReader.ReadFirstPage(stream));
        }
    }

    /// <summary>Verifies large clipboard pages are averaged and retain their aspect ratio.</summary>
    [TestMethod]
    public void ResizeToFitAveragesPixels()
    {
        using MemoryStream stream = new(CreateCompressedPage(4, 2, [1, 255, 255, 0, 0, 0, 255, 0, 1, 0, 0, 255]));
        PwgRasterPage page = PwgRasterReader.ReadFirstPage(stream).ResizeToFit(2);
        Assert.AreEqual(2, page.Width);
        Assert.AreEqual(1, page.Height);
        CollectionAssert.AreEqual(new byte[] { 0, 127, 127, 255, 255, 0, 0, 255 }, page.Pixels.ToArray());
        Assert.AreSame(page, page.ResizeToFit(2));
    }

    /// <summary>Verifies every compressed page is consumed in order with white padding for narrower pages.</summary>
    [TestMethod]
    public void ReadStackedPagesCombinesFourPages()
    {
        byte[] first = CreateCompressedPage(3, 1, [0, 2, 255, 0, 0]);
        byte[] second = CreateCompressedPage(1, 1, [0, 0, 0, 255, 0]);
        byte[] third = CreateCompressedPage(3, 1, [0, 2, 0, 0, 255]);
        byte[] fourth = CreateCompressedPage(3, 1, [0, 2, 255, 255, 0]);
        using MemoryStream stream = new([.. first, .. second.AsSpan(4), .. third.AsSpan(4), .. fourth.AsSpan(4)]);
        PwgRasterPage page = PwgRasterReader.ReadStackedPages(stream);
        Assert.AreEqual(3, page.Width);
        Assert.AreEqual(4, page.Height);
        CollectionAssert.AreEqual(new byte[]
        {
            0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255,
            255, 255, 255, 255, 0, 255, 0, 255, 255, 255, 255, 255,
            255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255,
            0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255,
        }, page.Pixels.ToArray());
    }

    /// <summary>Verifies a broken later page cannot silently produce a partial clipboard document.</summary>
    [TestMethod]
    public void ReadStackedPagesRejectsTruncatedLaterPage()
    {
        byte[] first = CreateCompressedPage(1, 1, [0, 0, 255, 0, 0]);
        byte[] second = CreateCompressedPage(1, 1, [0, 0, 255]);
        using MemoryStream stream = new([.. first, .. second.AsSpan(4)]);
        Assert.ThrowsExactly<InvalidDataException>(() => PwgRasterReader.ReadStackedPages(stream));
        using MemoryStream header = new([.. first, 0]);
        Assert.ThrowsExactly<InvalidDataException>(() => PwgRasterReader.ReadStackedPages(header));
    }

    private static byte[] CreateCompressedPage(int width, int height, byte[] payload)
    {
        byte[] data = new byte[4 + 1796 + payload.Length];
        "RaS2"u8.CopyTo(data);
        Span<byte> header = data.AsSpan(4, 1796);
        Write(header, 372, width);
        Write(header, 376, height);
        Write(header, 384, 8);
        Write(header, 388, 24);
        Write(header, 392, width * 3);
        Write(header, 400, 19);
        payload.CopyTo(data, 1800);
        return data;
    }

    private static void Write(Span<byte> destination, int offset, int value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset, 4), checked((uint)value));
    }
}
