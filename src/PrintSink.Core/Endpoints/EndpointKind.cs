namespace PrintSink.Core.Endpoints;

/// <summary>
/// Identifies a PrintSink virtual endpoint.
/// </summary>
public enum EndpointKind
{
    /// <summary>
    /// PDF file endpoint.
    /// </summary>
    Pdf,

    /// <summary>
    /// XPS file endpoint.
    /// </summary>
    Xps,

    /// <summary>
    /// PostScript file endpoint.
    /// </summary>
    PostScript,

    /// <summary>
    /// Cloud sink endpoint.
    /// </summary>
    Cloud,

    /// <summary>
    /// PWG Raster file endpoint.
    /// </summary>
    PwgRaster,

    /// <summary>
    /// PCLm file endpoint.
    /// </summary>
    Pclm,

    /// <summary>
    /// Stacks all rendered pages into one image on the Windows clipboard.
    /// </summary>
    Clipboard,
}
