using PrintSink.Core.Endpoints;
using PrintSink.Core.Pdl;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Devices.Printers;

namespace PrintSink.App;

/// <summary>
/// Provisions the package's virtual printer queues through the Windows virtual-printer API.
/// </summary>
internal static partial class VirtualPrinterInstaller
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidPrinterName = 1801;
    private const int ErrorAccessDenied = 5;
    private const string EntryPoint = "PrintSink.Tasks.VirtualPrinterBackgroundTask";
    private const int InstallationMaximumAttempts = 6;
    private const uint PrinterControlPurge = 3;
    private const uint PrinterInfoLevel = 4;
    private const PrinterEnumerationFlags EnumerationScope =
        PrinterEnumerationFlags.Local | PrinterEnumerationFlags.Connections;
    private static readonly TimeSpan InstallationRetryDelay = TimeSpan.FromSeconds(2);

    internal static async Task InstallAllAsync(CancellationToken cancellationToken)
    {
        string? blockerMessage = GetProvisioningBlockerMessage();
        if (blockerMessage is not null)
        {
            throw new InvalidOperationException(blockerMessage);
        }

        foreach (VirtualEndpoint endpoint in EndpointCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InstallEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string? GetProvisioningBlockerMessage()
    {
        return GetProvisioningBlockerMessage(Package.Current.InstalledLocation.Path);
    }

    internal static string? GetProvisioningBlockerMessage(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);

        if (IsLooseDevelopmentLayout(packageRoot))
        {
            return $"Virtual printer provisioning requires an installed MSIX package. The current PrintSink package is a loose development layout at '{packageRoot}'; install a signed MSIX, then run 'dotnet run --project src\\PrintSink.Cli -- queues install'.";
        }

        return null;
    }

    internal static async Task RemoveAllAsync(CancellationToken cancellationToken)
    {
        string packageFamilyName = Package.Current.Id.FamilyName;
        HashSet<string> installedPrinters = VirtualPrinterManager
            .FindAllVirtualPrinters(packageFamilyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        MoveDefaultPrinterBeforeRemoval(installedPrinters);

        foreach (VirtualEndpoint endpoint in EndpointCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!installedPrinters.Contains(endpoint.QueueName))
            {
                continue;
            }

            TryPurgePrintJobs(endpoint.QueueName);

            await VirtualPrinterManager
                .RemoveVirtualPrinterAsync(endpoint.QueueName)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static string? ChooseReplacementDefaultPrinter(
        string? currentDefaultPrinter,
        IReadOnlySet<string> printSinkPrinters,
        IEnumerable<string> installedPrinters)
    {
        ArgumentNullException.ThrowIfNull(printSinkPrinters);
        ArgumentNullException.ThrowIfNull(installedPrinters);

        if (string.IsNullOrWhiteSpace(currentDefaultPrinter)
            || !printSinkPrinters.Contains(currentDefaultPrinter))
        {
            return null;
        }

        return installedPrinters.FirstOrDefault(printer => !printSinkPrinters.Contains(printer));
    }

    private static void MoveDefaultPrinterBeforeRemoval(IReadOnlySet<string> printSinkPrinters)
    {
        string? replacement = ChooseReplacementDefaultPrinter(
            TryGetDefaultPrinter(),
            printSinkPrinters,
            ReadPrinterNames());
        if (string.IsNullOrWhiteSpace(replacement))
        {
            return;
        }

        if (!SetDefaultPrinter(replacement))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static string? TryGetDefaultPrinter()
    {
        uint bufferSize = 0;
        if (GetDefaultPrinter(null, ref bufferSize))
        {
            return null;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorFileNotFound || bufferSize == 0)
        {
            return null;
        }

        if (error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        char[] buffer = new char[bufferSize];
        if (!GetDefaultPrinter(buffer, ref bufferSize))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new string(buffer).TrimEnd('\0');
    }

    private static string[] ReadPrinterNames()
    {
        bool measured = EnumPrinters(
            EnumerationScope,
            null,
            PrinterInfoLevel,
            0,
            0,
            out uint needed,
            out _);
        if (!measured && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (needed == 0)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(
                EnumerationScope,
                null,
                PrinterInfoLevel,
                buffer,
                needed,
                out _,
                out uint returned))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            int itemSize = Marshal.SizeOf<PrinterInfo4>();
            string[] names = new string[returned];
            for (int index = 0; index < names.Length; index++)
            {
                nint item = nint.Add(buffer, index * itemSize);
                PrinterInfo4 printerInfo = Marshal.PtrToStructure<PrinterInfo4>(item);
                names[index] = printerInfo.PrinterName ?? string.Empty;
            }

            return [.. names.Where(name => !string.IsNullOrWhiteSpace(name))];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TryPurgePrintJobs(string printerName)
    {
        if (!OpenPrinter(printerName, out nint printerHandle, 0))
        {
            int openError = Marshal.GetLastPInvokeError();
            if (openError is ErrorFileNotFound or ErrorInvalidPrinterName or ErrorAccessDenied)
            {
                return;
            }

            throw new Win32Exception(openError);
        }

        try
        {
            if (!SetPrinter(printerHandle, 0, 0, PrinterControlPurge))
            {
                int purgeError = Marshal.GetLastPInvokeError();
                if (purgeError is ErrorFileNotFound or ErrorInvalidPrinterName or ErrorAccessDenied)
                {
                    return;
                }

                throw new Win32Exception(purgeError);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    private static VirtualPrinterInstallationParameters CreateParameters(VirtualEndpoint endpoint)
    {
        VirtualPrinterInstallationParameters parameters = new()
        {
            PrinterName = endpoint.QueueName,
            PrinterUri = endpoint.PrinterUri,
            PrintDeviceCapabilitiesPackageRelativeFilePath = GetPdcPath(endpoint.Kind),
            PrintDeviceResourcesPackageRelativeFilePath = GetPdrPath(endpoint.Kind),
            PreferredInputFormat = GetPreferredInputFormat(endpoint.PreferredInputFormat),
            EntryPoint = EntryPoint,
        };

        if (endpoint.RequiresTargetFile)
        {
            if (endpoint.OutputExtensions.Count == 0)
            {
                throw new InvalidOperationException($"Endpoint '{endpoint.QueueName}' requires at least one output extension.");
            }

            foreach (string extension in endpoint.OutputExtensions)
            {
                parameters.OutputFileExtensions.Add(extension.TrimStart('.'));
            }
        }

        foreach (PdlFormat passthroughFormat in endpoint.PassthroughFormats)
        {
            parameters.SupportedInputFormats.Add(CreateSupportedInputFormat(passthroughFormat));
        }

        return parameters;
    }

    private static async Task InstallEndpointAsync(VirtualEndpoint endpoint, CancellationToken cancellationToken)
    {
        VirtualPrinterInstallationParameters parameters = CreateParameters(endpoint);
        for (int attempt = 1; ; attempt++)
        {
            VirtualPrinterInstallationResult result = await VirtualPrinterManager
                .InstallVirtualPrinterAsync(parameters)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (IsSuccessfulInstallation(result.Status))
            {
                return;
            }

            if (attempt < InstallationMaximumAttempts && IsRetriableInstallationFailure(result.Status))
            {
                await Task.Delay(InstallationRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            string error = result.ExtendedError?.Message ?? "No extended error was reported.";
            throw new InvalidOperationException(
                $"Failed to install virtual printer '{endpoint.QueueName}' after {attempt} attempt(s): {result.Status}. {error}");
        }
    }

    private static bool IsSuccessfulInstallation(VirtualPrinterInstallationStatus status)
    {
        return status is VirtualPrinterInstallationStatus.InstallationSucceeded
            or VirtualPrinterInstallationStatus.PrinterAlreadyInstalled;
    }

    private static bool IsRetriableInstallationFailure(VirtualPrinterInstallationStatus status)
    {
        return status is VirtualPrinterInstallationStatus.PrinterInstallationFailed;
    }

    private static VirtualPrinterSupportedFormat CreateSupportedInputFormat(PdlFormat format)
    {
        return new VirtualPrinterSupportedFormat(
            PdlFormatInfo.GetContentType(format),
            PdlFormatInfo.GetMaxSupportedVersion(format));
    }

    private static VirtualPrinterPreferredInputFormat GetPreferredInputFormat(PdlFormat format)
    {
        return format switch
        {
            PdlFormat.Oxps => VirtualPrinterPreferredInputFormat.OpenXps,
            PdlFormat.PostScript => VirtualPrinterPreferredInputFormat.PostScript,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported preferred input format."),
        };
    }

    private static string GetPdcPath(EndpointKind kind)
    {
        return $"Config\\{GetConfigurationStem(kind)}.pdc.xml";
    }

    private static string GetPdrPath(EndpointKind kind)
    {
        return $"Config\\{GetConfigurationStem(kind)}.pdr.xml";
    }

    private static string GetConfigurationStem(EndpointKind kind)
    {
        return kind switch
        {
            EndpointKind.Pdf => "PrinterPdf",
            EndpointKind.Xps => "PrinterXps",
            EndpointKind.PostScript => "PrinterPostScript",
            EndpointKind.Cloud => "PrinterCloud",
            EndpointKind.PwgRaster => "PrinterPwgRaster",
            EndpointKind.Pclm => "PrinterPclm",
            EndpointKind.Clipboard => "PrinterPwgRaster",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported endpoint kind."),
        };
    }

    private static bool IsLooseDevelopmentLayout(string packageRoot)
    {
        string normalized = packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(normalized, "AppxSignature.p7x")))
        {
            return false;
        }

        return normalized.EndsWith(
                string.Concat(Path.DirectorySeparatorChar, "AppX"),
                StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                string.Concat(Path.DirectorySeparatorChar, "bin", Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrinters(
        PrinterEnumerationFlags flags,
        string? name,
        uint level,
        nint printerInfo,
        uint bufferSize,
        out uint needed,
        out uint returned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDefaultPrinter(
        [Out] char[]? buffer,
        ref uint bufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(
        string printerName,
        out nint printerHandle,
        nint printerDefaults);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultPrinter(string printerName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("winspool.drv", EntryPoint = "SetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPrinter(
        nint printerHandle,
        uint level,
        nint printerInfo,
        uint command);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint printerHandle);
}
