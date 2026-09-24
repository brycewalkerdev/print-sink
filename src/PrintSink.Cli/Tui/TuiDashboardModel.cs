using PrintSink.Core.Endpoints;
using PrintSink.Core.Diagnostics;
using PrintSink.Core.Pdl;
using PrintSink.Core.Processing;
using PrintSink.Core.Settings;

namespace PrintSink.Cli.Tui;

internal sealed class TuiDashboardModel
{
    private TuiDashboardModel(
        TuiAssetValidation manifest,
        TuiAssetValidation[] printDeviceCapabilities,
        TuiRouteCheck[] routeChecks,
        PrinterQueueSnapshot installedQueues,
        IReadOnlyList<DiagnosticEventRecord> diagnosticEvents)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(printDeviceCapabilities);
        ArgumentNullException.ThrowIfNull(routeChecks);
        ArgumentNullException.ThrowIfNull(installedQueues);
        ArgumentNullException.ThrowIfNull(diagnosticEvents);

        Manifest = manifest;
        PrintDeviceCapabilities = printDeviceCapabilities;
        RouteChecks = routeChecks;
        InstalledQueues = installedQueues;
        DiagnosticEvents = diagnosticEvents;
    }

    internal TuiAssetValidation Manifest { get; }

    internal IReadOnlyList<TuiAssetValidation> PrintDeviceCapabilities { get; }

    internal IReadOnlyList<TuiRouteCheck> RouteChecks { get; }

    internal PrinterQueueSnapshot InstalledQueues { get; }

    internal IReadOnlyList<DiagnosticEventRecord> DiagnosticEvents { get; }

    internal static async Task<TuiDashboardModel> LoadAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string diagnosticsRootDirectory = ResolveDiagnosticsRootDirectory(workingDirectory);
        using LocalDiagnosticEventStore diagnosticEventStore = new(diagnosticsRootDirectory);
        return await LoadAsync(
                workingDirectory,
                diagnosticEventStore,
                InstalledPrinterReader.Read,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TuiDashboardModel> LoadAsync(
        string workingDirectory,
        Func<PrinterQueueSnapshot> readInstalledQueues,
        CancellationToken cancellationToken)
    {
        string diagnosticsRootDirectory = ResolveDiagnosticsRootDirectory(workingDirectory);
        using LocalDiagnosticEventStore diagnosticEventStore = new(diagnosticsRootDirectory);
        return await LoadAsync(
                workingDirectory,
                diagnosticEventStore,
                readInstalledQueues,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TuiDashboardModel> LoadAsync(
        string workingDirectory,
        IDiagnosticEventStore diagnosticEventStore,
        CancellationToken cancellationToken)
    {
        return await LoadAsync(
                workingDirectory,
                diagnosticEventStore,
                InstalledPrinterReader.Read,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TuiDashboardModel> LoadAsync(
        string workingDirectory,
        IDiagnosticEventStore diagnosticEventStore,
        Func<PrinterQueueSnapshot> readInstalledQueues,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(diagnosticEventStore);
        ArgumentNullException.ThrowIfNull(readInstalledQueues);

        string appDirectory = ResolveAppDirectory(workingDirectory);
        string manifestPath = Path.Combine(appDirectory, "Package.appxmanifest");
        ManifestLintResult manifestResult = ManifestLinter.Lint(manifestPath);
        TuiAssetValidation manifest = new(
            "Manifest",
            manifestPath,
            manifestResult.Succeeded,
            manifestResult.Messages);

        TuiAssetValidation[] pdcValidations = ValidatePrintDeviceCapabilities(appDirectory);
        TuiRouteCheck[] routeChecks = await RunRouteChecksAsync(cancellationToken)
            .ConfigureAwait(false);
        PrinterQueueSnapshot installedQueues = readInstalledQueues();
        IReadOnlyList<DiagnosticEventRecord> diagnosticEvents = await diagnosticEventStore
            .ReadRecentAsync(8, cancellationToken)
            .ConfigureAwait(false);

        return new TuiDashboardModel(manifest, pdcValidations, routeChecks, installedQueues, diagnosticEvents);
    }

    private static TuiAssetValidation[] ValidatePrintDeviceCapabilities(string appDirectory)
    {
        string configDirectory = Path.Combine(appDirectory, "Config");
        List<TuiAssetValidation> validations = [];
        foreach (VirtualEndpoint endpoint in EndpointCatalog.All)
        {
            string assetName = GetConfigAssetName(endpoint.Kind);
            string pdcPath = Path.Combine(configDirectory, $"Printer{assetName}.pdc.xml");
            string pdrPath = Path.Combine(configDirectory, $"Printer{assetName}.pdr.xml");
            ValidationResult result = PdcValidator.Validate(pdcPath, pdrPath);
            validations.Add(new TuiAssetValidation(assetName, pdcPath, result.Succeeded, result.Messages));
        }

        return [.. validations];
    }

    private static async Task<TuiRouteCheck[]> RunRouteChecksAsync(CancellationToken cancellationToken)
    {
        List<TuiRouteCheck> checks = [];
        foreach (VirtualEndpoint endpoint in EndpointCatalog.All)
        {
            checks.Add(await RunRouteCheckAsync(endpoint, cancellationToken).ConfigureAwait(false));
        }

        return [.. checks];
    }

    private static async Task<TuiRouteCheck> RunRouteCheckAsync(
        VirtualEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        CapturingSink cloudSink = new();
        EndpointSinkResolver sinkResolver = new(new Dictionary<EndpointKind, ISink>
        {
            [EndpointKind.Pdf] = new TargetStreamSink(),
            [EndpointKind.Xps] = new TargetStreamSink(),
            [EndpointKind.PostScript] = new TargetStreamSink(),
            [EndpointKind.PwgRaster] = new TargetStreamSink(),
            [EndpointKind.Pclm] = new TargetStreamSink(),
            [EndpointKind.Clipboard] = new TargetStreamSink(),
            [EndpointKind.Cloud] = cloudSink,
        });
        string contentType = PdlFormatInfo.GetContentType(endpoint.PreferredInputFormat);
        FixtureVirtualPrinterJob job = new(contentType, endpoint, null, null);
        VirtualPrinterJobProcessor processor = new(new PdlRouter(), new FixturePdlConverter(), sinkResolver);

        try
        {
            VirtualPrinterJobResult result = await processor.ProcessAsync(job, cancellationToken)
                .ConfigureAwait(false);
            long outputBytes = endpoint.Kind == EndpointKind.Cloud
                ? cloudSink.BytesWritten
                : job.OutputBytes;

            return new TuiRouteCheck(
                endpoint.QueueName,
                contentType,
                result.Plan.ActionKind,
                result.Plan.ConversionKind,
                result.Status,
                outputBytes);
        }
        finally
        {
            job.DeleteTemporaryOutput();
        }
    }

    private static string ResolveAppDirectory(string workingDirectory)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(workingDirectory));
        while (directory is not null)
        {
            string appDirectory = Path.Combine(directory.FullName, "src", "PrintSink.App");
            if (File.Exists(Path.Combine(appDirectory, "Package.appxmanifest")))
            {
                return appDirectory;
            }

            directory = directory.Parent;
        }

        return Path.Combine(Path.GetFullPath(workingDirectory), "src", "PrintSink.App");
    }

    internal static string ResolveDiagnosticsRootDirectory(string workingDirectory)
    {
        string? packageSettingsDirectory = TryResolveInstalledPackageSettingsDirectory();
        if (packageSettingsDirectory is not null)
        {
            return packageSettingsDirectory;
        }

        return Path.Combine(ResolveRepositoryRoot(workingDirectory), ".printsink", "Settings");
    }

    private static string ResolveRepositoryRoot(string workingDirectory)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(workingDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PrintSink.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(workingDirectory);
    }

    private static string? TryResolveInstalledPackageSettingsDirectory()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return null;
        }

        string packagesDirectory = Path.Combine(localApplicationData, "Packages");
        if (!Directory.Exists(packagesDirectory))
        {
            return null;
        }

        DirectoryInfo? packageDirectory = Directory
            .EnumerateDirectories(packagesDirectory, "PrintSink_*")
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .FirstOrDefault();
        if (packageDirectory is null)
        {
            return null;
        }

        string localStateDirectory = Path.Combine(packageDirectory.FullName, "LocalState");
        return PackagedSettingsDirectory.GetRootDirectory(localStateDirectory);
    }

    private static string GetConfigAssetName(EndpointKind kind)
    {
        return kind switch
        {
            EndpointKind.Pdf => "Pdf",
            EndpointKind.Xps => "Xps",
            EndpointKind.PostScript => "PostScript",
            EndpointKind.Cloud => "Cloud",
            EndpointKind.PwgRaster => "PwgRaster",
            EndpointKind.Pclm => "Pclm",
            EndpointKind.Clipboard => "Clipboard",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown endpoint kind."),
        };
    }
}
