using PrintSink.Core.Endpoints;

namespace PrintSink.Cli;

/// <summary>
/// Parses endpoint names accepted by CLI commands.
/// </summary>
internal static class EndpointParser
{
    /// <summary>
    /// Tries to parse a command-line endpoint name.
    /// </summary>
    /// <param name="text">The endpoint text.</param>
    /// <param name="endpointKind">The parsed endpoint kind when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string text, out EndpointKind endpointKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        string normalized = text.Trim().Replace("_", "-", StringComparison.Ordinal).ToUpperInvariant();
        endpointKind = normalized switch
        {
            "PDF" => EndpointKind.Pdf,
            "XPS" => EndpointKind.Xps,
            "POSTSCRIPT" or "POST-SCRIPT" or "PS" => EndpointKind.PostScript,
            "CLOUD" => EndpointKind.Cloud,
            "PWG" or "PWG-RASTER" or "PWGRASTER" => EndpointKind.PwgRaster,
            "PCLM" or "PCL-M" => EndpointKind.Pclm,
            "CLIPBOARD" or "IMAGE-CLIPBOARD" or "CLIP" => EndpointKind.Clipboard,
            _ => default,
        };

        return endpointKind != default || string.Equals(normalized, "PDF", StringComparison.Ordinal);
    }
}
