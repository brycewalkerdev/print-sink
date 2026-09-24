using System.Buffers.Binary;

namespace PrintSink.Core.Pdl;

/// <summary>Decodes the common 8-bit RGB, RGBA, and grayscale PWG Raster forms.</summary>
public static class PwgRasterReader
{
    private const int HeaderSize = 1796;

    /// <summary>Reads the first page from a PWG Raster stream.</summary>
    public static PwgRasterPage ReadFirstPage(Stream stream)
    {
        byte[] data = ReadDocument(stream);
        int offset = 4;
        return ReadPage(data, ref offset);
    }

    /// <summary>Stacks every page vertically, sizing each page before combining it.</summary>
    public static PwgRasterPage ReadStackedPages(Stream stream, int maximumPageDimension = 2400)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPageDimension);
        byte[] data = ReadDocument(stream);
        List<PwgRasterPage> pages = [];
        int offset = 4;
        long retainedPixels = 0;
        while (offset < data.Length)
        {
            PwgRasterPage page = ReadPage(data, ref offset).ResizeToFit(maximumPageDimension);
            retainedPixels += (long)page.Width * page.Height;
            if (retainedPixels > 128_000_000)
            {
                throw new InvalidDataException("The document is too large for a single clipboard image.");
            }

            pages.Add(page);
        }

        return PwgRasterPage.StackVertically(pages);
    }

    private static byte[] ReadDocument(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] data = buffer.ToArray();
        if (data.Length < 4 + HeaderSize)
        {
            throw new InvalidDataException("The PWG Raster stream is too short.");
        }

        uint signature = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (signature is not (0x52615332 or 0x52615333 or 0x32536152 or 0x33536152))
        {
            throw new InvalidDataException("The stream is not PWG Raster (RaS2/RaS3).");
        }

        return data;
    }

    private static PwgRasterPage ReadPage(ReadOnlySpan<byte> input, ref int pageOffset)
    {
        uint signature = BinaryPrimitives.ReadUInt32BigEndian(input);
        bool littleEndian = signature is 0x32536152 or 0x33536152;
        int headerOffset = pageOffset;
        if (input.Length - headerOffset < HeaderSize)
        {
            throw new InvalidDataException("The PWG Raster page header is truncated.");
        }
        int width = ReadUInt32(input, headerOffset + 372, littleEndian);
        int height = ReadUInt32(input, headerOffset + 376, littleEndian);
        int bitsPerColor = ReadUInt32(input, headerOffset + 384, littleEndian);
        int bitsPerPixel = ReadUInt32(input, headerOffset + 388, littleEndian);
        int bytesPerLine = ReadUInt32(input, headerOffset + 392, littleEndian);
        int colorOrder = ReadUInt32(input, headerOffset + 396, littleEndian);
        int colorSpace = ReadUInt32(input, headerOffset + 400, littleEndian);

        if (width <= 0 || height <= 0 || width > 100_000 || height > 100_000)
        {
            throw new InvalidDataException("The PWG Raster dimensions are invalid.");
        }

        if (bitsPerColor != 8 || colorOrder != 0 || bitsPerPixel is not (8 or 24 or 32))
        {
            throw new NotSupportedException("Only chunky, 8-bit PWG Raster is supported.");
        }

        if ((colorSpace is 0 or 3 or 18 && bitsPerPixel != 8)
            || (colorSpace is 1 or 19 && bitsPerPixel != 24)
            || (colorSpace == 2 && bitsPerPixel != 32))
        {
            throw new InvalidDataException("The PWG color space and pixel width do not match.");
        }

        int sourceBytesPerPixel = bitsPerPixel / 8;
        if (bytesPerLine < checked(width * sourceBytesPerPixel) || bytesPerLine % sourceBytesPerPixel != 0)
        {
            throw new InvalidDataException("The PWG Raster row size is invalid.");
        }

        int dataOffset = checked(headerOffset + HeaderSize);
        byte[] pixels = new byte[checked(width * height * 4)];
        byte[] rowBuffer = new byte[bytesPerLine];
        bool compressed = signature is 0x52615332 or 0x32536152;
        int repeatedRows = 0;
        for (int y = 0; y < height; y++)
        {
            if (repeatedRows == 0)
            {
                if (compressed)
                {
                    repeatedRows = Take(input, ref dataOffset, 1)[0] + 1;
                    if (repeatedRows > height - y)
                    {
                        throw new InvalidDataException("The PWG Raster row repetition exceeds the page height.");
                    }

                    DecodeRow(input, ref dataOffset, rowBuffer, sourceBytesPerPixel);
                }
                else
                {
                    Take(input, ref dataOffset, bytesPerLine).CopyTo(rowBuffer);
                    repeatedRows = 1;
                }
            }

            repeatedRows--;
            ReadOnlySpan<byte> row = rowBuffer;
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = x * sourceBytesPerPixel;
                int targetIndex = (y * width + x) * 4;
                byte r;
                byte g;
                byte b;
                byte a = 255;
                if (colorSpace is 0 or 3 or 18)
                {
                    r = g = b = colorSpace == 3 ? (byte)(255 - row[sourceIndex]) : row[sourceIndex];
                }
                else if (colorSpace is 2)
                {
                    r = row[sourceIndex];
                    g = row[sourceIndex + 1];
                    b = row[sourceIndex + 2];
                    a = row[sourceIndex + 3];
                }
                else if (colorSpace is 1 or 19)
                {
                    r = row[sourceIndex];
                    g = row[sourceIndex + 1];
                    b = row[sourceIndex + 2];
                }
                else
                {
                    throw new NotSupportedException($"PWG color space {colorSpace} is not supported.");
                }

                pixels[targetIndex] = b;
                pixels[targetIndex + 1] = g;
                pixels[targetIndex + 2] = r;
                pixels[targetIndex + 3] = a;
            }
        }

        pageOffset = dataOffset;
        return new PwgRasterPage(width, height, width * 4, pixels);
    }

    private static void DecodeRow(ReadOnlySpan<byte> input, ref int offset, Span<byte> row, int bytesPerPixel)
    {
        int written = 0;
        while (written < row.Length)
        {
            byte control = Take(input, ref offset, 1)[0];
            int count = control < 128 ? control + 1 : 257 - control;
            int length = checked(count * bytesPerPixel);
            if (length > row.Length - written)
            {
                throw new InvalidDataException("The PWG Raster pixel run exceeds the row size.");
            }

            if (control < 128)
            {
                ReadOnlySpan<byte> pixel = Take(input, ref offset, bytesPerPixel);
                for (int i = 0; i < count; i++)
                {
                    pixel.CopyTo(row.Slice(written + i * bytesPerPixel, bytesPerPixel));
                }
            }
            else
            {
                Take(input, ref offset, length).CopyTo(row.Slice(written, length));
            }

            written += length;
        }
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> input, ref int offset, int length)
    {
        if (length > input.Length - offset)
        {
            throw new InvalidDataException("The PWG Raster page is truncated.");
        }

        ReadOnlySpan<byte> result = input.Slice(offset, length);
        offset += length;
        return result;
    }

    private static int ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        uint value = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        return checked((int)value);
    }
}
