using PrintSink.Core.Endpoints;
using PrintSink.Core.Pdl;

namespace PrintSink.Core.Tests.Endpoints;

/// <summary>
/// Tests the built-in endpoint catalog.
/// </summary>
[TestClass]
internal sealed class EndpointCatalogTests
{
    private static readonly string[] XpsOutputExtensions = [".xps", ".oxps"];

    /// <summary>
    /// Verifies the endpoint display order.
    /// </summary>
    [TestMethod]
    public void AllReturnsExpectedEndpointsInDisplayOrder()
    {
        EndpointKind[] expected =
        [
            EndpointKind.Pdf,
            EndpointKind.Xps,
            EndpointKind.PostScript,
            EndpointKind.Cloud,
            EndpointKind.PwgRaster,
            EndpointKind.Pclm,
            EndpointKind.Clipboard,
        ];

        CollectionAssert.AreEqual(expected, EndpointCatalog.All.Select(endpoint => endpoint.Kind).ToArray());
    }

    /// <summary>
    /// Verifies that printer URIs uniquely identify endpoints.
    /// </summary>
    [TestMethod]
    public void AllHasUniquePrinterUris()
    {
        Uri[] printerUris = [.. EndpointCatalog.All.Select(endpoint => endpoint.PrinterUri)];
        int distinctCount = printerUris.Select(uri => uri.AbsoluteUri).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Assert.AreEqual(printerUris.Length, distinctCount);
    }

    /// <summary>
    /// Verifies that the cloud endpoint is not file-backed.
    /// </summary>
    [TestMethod]
    public void CloudEndpointDoesNotRequireTargetFile()
    {
        VirtualEndpoint endpoint = EndpointCatalog.GetByKind(EndpointKind.Cloud);

        Assert.IsFalse(endpoint.RequiresTargetFile);
        Assert.IsNull(endpoint.DefaultExtension);
        Assert.AreEqual(PdlFormat.Pdf, endpoint.TargetFormat);
    }

    /// <summary>Verifies that the clipboard endpoint is an application sink.</summary>
    [TestMethod]
    public void ClipboardEndpointIsAnImageApplicationSink()
    {
        VirtualEndpoint endpoint = EndpointCatalog.GetByKind(EndpointKind.Clipboard);

        Assert.IsFalse(endpoint.RequiresTargetFile);
        Assert.IsNull(endpoint.DefaultExtension);
        Assert.AreEqual(PdlFormat.PwgRaster, endpoint.TargetFormat);
    }

    /// <summary>
    /// Verifies that file endpoints declare file output metadata.
    /// </summary>
    [TestMethod]
    public void FileEndpointsRequireTargetFileAndExtension()
    {
        foreach (VirtualEndpoint endpoint in EndpointCatalog.All.Where(endpoint => endpoint.RequiresTargetFile))
        {
            Assert.IsTrue(endpoint.RequiresTargetFile);
            Assert.IsFalse(string.IsNullOrWhiteSpace(endpoint.DefaultExtension));
            Assert.IsNotEmpty(endpoint.OutputExtensions);
        }
    }

    /// <summary>
    /// Verifies the XPS endpoint exposes both classic XPS and OpenXPS file extensions.
    /// </summary>
    [TestMethod]
    public void XpsEndpointDeclaresXpsAndOxpsOutputExtensions()
    {
        VirtualEndpoint endpoint = EndpointCatalog.GetByKind(EndpointKind.Xps);

        CollectionAssert.AreEquivalent(
            XpsOutputExtensions,
            endpoint.OutputExtensions.ToArray());
    }

    /// <summary>
    /// Verifies URI resolution for registered endpoint addresses.
    /// </summary>
    [TestMethod]
    public void TryResolveAcceptsRegisteredUriCaseInsensitively()
    {
        Uri printerUri = new("PRINTSINK:PRINT-TO-PDF");

        bool resolved = EndpointCatalog.TryResolve(printerUri, out VirtualEndpoint? endpoint);

        Assert.IsTrue(resolved);
        Assert.IsNotNull(endpoint);
        Assert.AreEqual(EndpointKind.Pdf, endpoint.Kind);
    }
}
