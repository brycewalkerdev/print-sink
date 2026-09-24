namespace PrintSink.Core.Pdl;

/// <summary>Represents one decoded page from a PWG Raster stream.</summary>
public sealed class PwgRasterPage
{
    internal PwgRasterPage(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the BGRA stride.</summary>
    public int Stride { get; }

    /// <summary>Gets 32-bit BGRA pixels, top-to-bottom.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }

    /// <summary>Stacks pages in order on a white canvas, reducing oversized documents to clipboard limits.</summary>
    public static PwgRasterPage StackVertically(IReadOnlyList<PwgRasterPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        int width = pages.Max(page => page.Width);
        long totalHeight = pages.Sum(page => (long)page.Height);
        double scale = Math.Min(1, Math.Min(30000.0 / totalHeight,
            Math.Sqrt(24_000_000.0 / ((long)width * totalHeight))));
        PwgRasterPage[] sized = pages.Select(page => page.ResizeToFit(
            Math.Max(1, (int)(Math.Max(page.Width, page.Height) * scale)))).ToArray();
        width = sized.Max(page => page.Width);
        int height = checked(sized.Sum(page => page.Height));
        byte[] output = new byte[checked(width * height * 4)];
        Array.Fill(output, (byte)255);
        int top = 0;
        foreach (PwgRasterPage page in sized)
        {
            int left = (width - page.Width) / 2;
            for (int y = 0; y < page.Height; y++)
            {
                page.Pixels.Span.Slice(y * page.Stride, page.Width * 4)
                    .CopyTo(output.AsSpan(((top + y) * width + left) * 4, page.Width * 4));
            }

            top += page.Height;
        }

        return new PwgRasterPage(width, height, width * 4, output);
    }

    /// <summary>Limits image dimensions using area averaging while preserving its aspect ratio.</summary>
    public PwgRasterPage ResizeToFit(int maximumDimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDimension);
        int largest = Math.Max(Width, Height);
        if (largest <= maximumDimension)
        {
            return this;
        }

        int width = Math.Max(1, (int)((long)Width * maximumDimension / largest));
        int height = Math.Max(1, (int)((long)Height * maximumDimension / largest));
        byte[] output = new byte[checked(width * height * 4)];
        ReadOnlySpan<byte> input = Pixels.Span;
        for (int y = 0; y < height; y++)
        {
            int top = (int)((long)y * Height / height);
            int bottom = (int)((long)(y + 1) * Height / height);
            for (int x = 0; x < width; x++)
            {
                int left = (int)((long)x * Width / width);
                int right = (int)((long)(x + 1) * Width / width);
                for (int channel = 0; channel < 4; channel++)
                {
                    long sum = 0;
                    for (int sourceY = top; sourceY < bottom; sourceY++)
                    {
                        for (int sourceX = left; sourceX < right; sourceX++)
                        {
                            sum += input[sourceY * Stride + sourceX * 4 + channel];
                        }
                    }

                    output[(y * width + x) * 4 + channel] = (byte)(sum / ((long)(bottom - top) * (right - left)));
                }
            }
        }

        return new PwgRasterPage(width, height, width * 4, output);
    }
}
