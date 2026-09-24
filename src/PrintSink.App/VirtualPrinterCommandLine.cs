using Microsoft.Windows.AppLifecycle;
using PrintSink.Core.Diagnostics;
using PrintSink.Core.Endpoints;
using PrintSink.Core.Settings;
using PrintSink.Core.Watermark;
using System.Text;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;

namespace PrintSink.App;

/// <summary>
/// Handles package-identity commands that should run without showing the WinUI shell.
/// </summary>
internal static class VirtualPrinterCommandLine
{
    private const int Success = 0;
    private const int Failure = 1;

    internal static async Task<int?> RunIfRequestedAsync(
        IReadOnlyList<string> args,
        AppActivationArguments activationArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(activationArguments);

        List<string> commandArgs = [.. args, .. GetActivationArguments(activationArguments)];
        bool install = Contains(commandArgs, "--install-virtual-printers");
        bool remove = Contains(commandArgs, "--remove-virtual-printers");
        bool enableJobUi = Contains(commandArgs, "--enable-job-ui");
        bool disableJobUi = Contains(commandArgs, "--disable-job-ui");
        bool setTextWatermark = Contains(commandArgs, "--set-text-watermark");
        bool setImageWatermark = Contains(commandArgs, "--set-image-watermark");
        bool clearWatermark = Contains(commandArgs, "--clear-watermark");
        bool refreshCapabilities = Contains(commandArgs, "--refresh-capabilities");
        bool printPdfPassthrough = Contains(commandArgs, "--print-pdf-passthrough");
        bool printPdfPassthroughToPrinter = printPdfPassthrough && HasOption(commandArgs, "--printer");
        bool setDefaultCopies = Contains(commandArgs, "--set-default-copies");
        bool assertVirtualAttributeRead = Contains(commandArgs, "--assert-virtual-attribute-read");
        bool help = Contains(commandArgs, "--help") || Contains(commandArgs, "-h") || Contains(commandArgs, "-?");
        if (!install
            && !remove
            && !enableJobUi
            && !disableJobUi
            && !setTextWatermark
            && !setImageWatermark
            && !clearWatermark
            && !refreshCapabilities
            && !printPdfPassthrough
            && !setDefaultCopies
            && !assertVirtualAttributeRead
            && !help)
        {
            return null;
        }

        if (help
            && !install
            && !remove
            && !enableJobUi
            && !disableJobUi
            && !setTextWatermark
            && !setImageWatermark
            && !clearWatermark
            && !refreshCapabilities
            && !printPdfPassthrough
            && !setDefaultCopies
            && !assertVirtualAttributeRead)
        {
            WriteHelp();
            SetCommandLineExitCode(activationArguments, Success);
            return Success;
        }

        if ((install && remove)
            || (enableJobUi && disableJobUi)
            || ((setTextWatermark || setImageWatermark) && clearWatermark)
            || (printPdfPassthroughToPrinter && HasOption(commandArgs, "--endpoint")))
        {
            SetCommandLineExitCode(activationArguments, Failure);
            return Failure;
        }

        int exitCode = Failure;
        Deferral? deferral = GetCommandLineDeferral(activationArguments);
        try
        {
            EndpointKind endpointKind = EndpointKind.Pdf;
            bool needsEndpoint = setTextWatermark
                || setImageWatermark
                || clearWatermark
                || refreshCapabilities
                || (printPdfPassthrough && !printPdfPassthroughToPrinter)
                || setDefaultCopies
                || assertVirtualAttributeRead;
            if (needsEndpoint)
            {
                endpointKind = GetRequiredEndpointKind(commandArgs);
            }

            if (enableJobUi || disableJobUi)
            {
                await AppSettingsStoreFactory
                    .Create()
                    .SaveJobUiOptionsAsync(new(enableJobUi), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (setTextWatermark || setImageWatermark)
            {
                TextWatermark? text = setTextWatermark
                    ? CreateTextWatermark(GetRequiredOptionValue(commandArgs, "--text"))
                    : null;
                ImageWatermark? image = setImageWatermark
                    ? await CreateImageWatermarkAsync(
                            GetRequiredOptionValue(commandArgs, "--image"),
                            cancellationToken)
                        .ConfigureAwait(false)
                    : null;

                await SaveWatermarkAsync(
                        endpointKind,
                        new WatermarkOptions(true, text, image),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (clearWatermark)
            {
                await SaveWatermarkAsync(endpointKind, WatermarkOptions.Disabled, cancellationToken).ConfigureAwait(false);
            }

            if (refreshCapabilities)
            {
                WriteDiagnostic(InstalledVirtualPrinterReader.RefreshCapabilities(endpointKind));
            }

            if (printPdfPassthrough)
            {
                string printerName = printPdfPassthroughToPrinter
                    ? GetRequiredOptionValue(commandArgs, "--printer")
                    : EndpointCatalog.GetByKind(endpointKind).QueueName;
                (int printJobId, string providerDetail) = await PdlPassthroughPrintCommand
                    .PrintPdfToPrinterAsync(
                        printerName,
                        GetRequiredOptionValue(commandArgs, "--source"),
                        async (createdPrintJobId, createdProviderDetail, createdCancellationToken) =>
                        {
                            string createdDetail = $"printJobId={createdPrintJobId}; {createdProviderDetail}";
                            WriteDiagnostic($"PDF passthrough print target created: {createdDetail}");
                            await AppendDiagnosticAsync(
                                    "PDF passthrough print target created",
                                    printerName,
                                    createdDetail,
                                    createdCancellationToken)
                                .ConfigureAwait(false);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                string detail = $"printJobId={printJobId}; {providerDetail}";
                WriteDiagnostic($"PDF passthrough print job submitted: {detail}");
                await AppendDiagnosticAsync(
                        "PDF passthrough print job submitted",
                        printerName,
                        detail,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (setDefaultCopies)
            {
                string result = await UserDefaultPrintTicketEditor
                    .SetCopiesAsync(
                        endpointKind,
                        GetRequiredIntegerOptionValue(commandArgs, "--copies", 1, 999),
                        cancellationToken)
                    .ConfigureAwait(false);
                WriteDiagnostic(result);
                await AppendDiagnosticAsync(
                        "User default print ticket updated",
                        EndpointCatalog.GetByKind(endpointKind).QueueName,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (assertVirtualAttributeRead)
            {
                string result = InstalledVirtualPrinterReader.AssertAttributeReadMatchesPlatformBehavior(endpointKind);
                WriteDiagnostic(result);
                await AppendDiagnosticAsync(
                        "Virtual printer attribute read matched platform behavior",
                        EndpointCatalog.GetByKind(endpointKind).QueueName,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (install)
            {
                await VirtualPrinterInstaller.InstallAllAsync(cancellationToken).ConfigureAwait(false);
            }

            if (remove)
            {
                await VirtualPrinterInstaller.RemoveAllAsync(cancellationToken).ConfigureAwait(false);
            }

            exitCode = Success;
            return exitCode;
        }
        catch (Exception ex) when (AppExceptionPolicy.IsRecoverable(ex))
        {
            WriteDiagnostic(ex.ToString());
            return exitCode;
        }
        finally
        {
            SetCommandLineExitCode(activationArguments, exitCode);
            deferral?.Complete();
        }
    }

    private static TextWatermark CreateTextWatermark(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return new TextWatermark(text.Trim(), "Segoe UI", 48, 0.28, -30, 0, 0);
    }

    private static Task<ImageWatermark> CreateImageWatermarkAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        return WatermarkImageStorage.CreateImageWatermarkAsync(imagePath, 96, 96, 0.45, 0, 0, 0, cancellationToken);
    }

    private static async Task SaveWatermarkAsync(
        EndpointKind endpointKind,
        WatermarkOptions options,
        CancellationToken cancellationToken)
    {
        VirtualEndpoint endpoint = EndpointCatalog.GetByKind(endpointKind);
        await AppSettingsStoreFactory
            .Create()
            .SaveWatermarkOptionsAsync(endpoint.PrinterUri, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private static EndpointKind GetRequiredEndpointKind(IReadOnlyList<string> args)
    {
        string endpointValue = GetRequiredOptionValue(args, "--endpoint");
        return Enum.TryParse(endpointValue, ignoreCase: true, out EndpointKind endpointKind)
            ? endpointKind
            : throw new ArgumentException($"Unknown endpoint '{endpointValue}'.");
    }

    private static string GetRequiredOptionValue(IReadOnlyList<string> args, string option)
    {
        for (int index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                string value = args[index + 1];
                if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal))
                {
                    return value;
                }
            }
        }

        throw new ArgumentException($"Missing required {option} value.");
    }

    internal static string[] GetActivationArguments(AppActivationArguments activationArguments)
    {
        return activationArguments.Data switch
        {
            CommandLineActivatedEventArgs commandLineArgs => SplitArguments(commandLineArgs.Operation.Arguments),
            LaunchActivatedEventArgs launchArgs => SplitArguments(launchArgs.Arguments),
            _ => [],
        };
    }

    internal static string[] SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        List<string> result = [];
        StringBuilder current = new();
        bool inQuotes = false;
        bool argumentStarted = false;
        int backslashCount = 0;

        foreach (char character in arguments)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                current.Append('\\', backslashCount / 2);
                if (backslashCount % 2 == 0)
                {
                    inQuotes = !inQuotes;
                    argumentStarted = true;
                }
                else
                {
                    current.Append('"');
                    argumentStarted = true;
                }

                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                current.Append('\\', backslashCount);
                backslashCount = 0;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                AddArgument(result, current, ref argumentStarted);
                continue;
            }

            current.Append(character);
            argumentStarted = true;
        }

        if (backslashCount > 0)
        {
            current.Append('\\', backslashCount);
        }

        AddArgument(result, current, ref argumentStarted);
        return [.. result];
    }

    private static void AddArgument(List<string> result, StringBuilder current, ref bool argumentStarted)
    {
        if (!argumentStarted)
        {
            return;
        }

        result.Add(current.ToString());
        current.Clear();
        argumentStarted = false;
    }

    private static void SetCommandLineExitCode(AppActivationArguments activationArguments, int? exitCode)
    {
        if (exitCode is int value && activationArguments.Data is CommandLineActivatedEventArgs commandLineArgs)
        {
            commandLineArgs.Operation.ExitCode = value;
        }
    }

    private static Deferral? GetCommandLineDeferral(AppActivationArguments activationArguments)
    {
        return activationArguments.Data is CommandLineActivatedEventArgs commandLineArgs
            ? commandLineArgs.Operation.GetDeferral()
            : null;
    }

    internal static void WriteDiagnostic(string message)
    {
        string path = Path.Combine(Path.GetTempPath(), "PrintSink.App.headless.log");
        File.AppendAllText(path, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        System.Diagnostics.Trace.TraceError(message);
    }

    internal static void WriteStartupTrace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PRINTSINK_TRACE_STARTUP"), "1", StringComparison.Ordinal))
        {
            WriteDiagnostic(message);
        }
    }

    internal static void WriteHelp()
    {
        string help = string.Join(
            Environment.NewLine,
            "PrintSink packaged app commands:",
            "  --install-virtual-printers  Install PrintSink virtual printer queues.",
            "  --remove-virtual-printers   Remove PrintSink virtual printer queues.",
            "  --disable-job-ui            Process jobs without launching the foreground Job UI.",
            "  --enable-job-ui             Restore foreground Job UI launch behavior.",
            "  --set-text-watermark        Set a default text watermark for --endpoint.",
            "  --set-image-watermark       Set a default image watermark for --endpoint.",
            "  --clear-watermark           Clear the default watermark for --endpoint.",
            "  --refresh-capabilities      Refresh print capabilities for --endpoint.",
            "  --print-pdf-passthrough     Send a PDF through IppPrintDevice PDL passthrough.",
            "  --set-default-copies        Set default ticket copies for --endpoint.",
            "  --assert-virtual-attribute-read  Assert virtual-printer IPP attribute behavior.",
            "  --winrt-source-print        Open a WinRT print-source harness for E2E validation.",
            "  --endpoint <kind>           Endpoint kind: Pdf, Xps, PostScript, Cloud, PwgRaster, Pclm, Clipboard.",
            "  --printer <name>            Printer name used with --print-pdf-passthrough instead of --endpoint.",
            "  --text <value>              Text used with --set-text-watermark.",
            "  --image <path>              Image file used with --set-image-watermark.",
            "  --source <path>             Source file used with --print-pdf-passthrough.",
            "  --copies <count>            Copy count used with --set-default-copies.",
            "",
            "For visible operator help, run: dotnet run --project src\\PrintSink.Cli -- --help");
        Console.Out.WriteLine(help);
        WriteDiagnostic(help);
    }

    internal static string Describe(AppActivationArguments activationArguments)
    {
        ArgumentNullException.ThrowIfNull(activationArguments);

        return activationArguments.Data switch
        {
            CommandLineActivatedEventArgs commandLineArgs => $"Activation data: {activationArguments.Data.GetType().FullName}; command line: {commandLineArgs.Operation.Arguments}",
            LaunchActivatedEventArgs launchArgs => $"Activation data: {activationArguments.Data.GetType().FullName}; launch args: {launchArgs.Arguments}",
            null => "Activation data: <null>",
            _ => $"Activation data: {activationArguments.Data.GetType().FullName}",
        };
    }

    private static bool Contains(IReadOnlyList<string> args, string value)
    {
        return args.Any(arg => string.Equals(arg, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasOption(IReadOnlyList<string> args, string option)
    {
        return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetRequiredIntegerOptionValue(
        IReadOnlyList<string> args,
        string option,
        int minimumValue,
        int maximumValue)
    {
        string value = GetRequiredOptionValue(args, option);
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int result)
            || result < minimumValue
            || result > maximumValue)
        {
            throw new ArgumentException($"{option} must be an integer from {minimumValue} through {maximumValue}.");
        }

        return result;
    }

    private static async Task AppendDiagnosticAsync(
        string message,
        string endpoint,
        string detail,
        CancellationToken cancellationToken)
    {
        using LocalDiagnosticEventStore diagnosticEventStore = AppSettingsStoreFactory.CreateDiagnosticEventStore();
        await diagnosticEventStore
            .AppendAsync(
                new DiagnosticEventRecord(
                    DateTimeOffset.UtcNow,
                    DiagnosticEventSeverity.Information,
                    nameof(VirtualPrinterCommandLine),
                    message,
                    endpoint,
                    detail),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
