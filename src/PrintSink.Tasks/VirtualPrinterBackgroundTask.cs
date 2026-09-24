using Windows.ApplicationModel.Background;
using PrintSink.Core.Diagnostics;
using PrintSink.Core.Endpoints;
using PrintSink.Core.Pdl;
using PrintSink.Core.Processing;
using PrintSink.Core.Settings;
using Windows.Graphics.Printing.Workflow;

namespace PrintSink.Tasks;

/// <summary>
/// Handles virtual printer jobs submitted to PrintSink queues.
/// </summary>
public sealed class VirtualPrinterBackgroundTask : IBackgroundTask
{
    private static readonly TimeSpan DiagnosticWriteTimeout = TimeSpan.FromSeconds(2);

    private readonly BackgroundTaskHandlerState state = new();

    /// <inheritdoc />
    public void Run(IBackgroundTaskInstance taskInstance)
    {
        ArgumentNullException.ThrowIfNull(taskInstance);

        state.Attach(taskInstance);
        AppendDiagnostic(
            "Virtual printer task activated",
            string.Empty,
            $"trigger={taskInstance.TriggerDetails?.GetType().FullName ?? "<null>"}");

        if (taskInstance.TriggerDetails is not PrintWorkflowVirtualPrinterTriggerDetails virtualPrinterDetails)
        {
            AppendDiagnostic(
                "Virtual printer task ignored",
                string.Empty,
                $"trigger={taskInstance.TriggerDetails?.GetType().FullName ?? "<null>"}");
            state.CompleteWhenIdle();
            return;
        }

        PrintWorkflowVirtualPrinterSession session = virtualPrinterDetails.VirtualPrinterSession;
        session.VirtualPrinterDataAvailable += OnVirtualPrinterDataAvailable;
        session.Start();
        AppendDiagnostic(
            "Virtual printer session started",
            GetPrinterName(session),
            $"uri={session.Printer.PrinterUri}");
    }

    private void OnVirtualPrinterDataAvailable(
        PrintWorkflowVirtualPrinterSession sender,
        PrintWorkflowVirtualPrinterDataAvailableEventArgs args)
    {
        try
        {
            bool handled = state.Run(() =>
            {
                try
                {
                    ProcessJobAsync(sender, args).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (BackgroundTaskExceptionPolicy.IsRecoverable(ex))
                {
                    AppendDiagnostic(
                        "Virtual printer job failed",
                        GetPrinterName(sender),
                        ex.ToString());
                    TryCompleteJob(args, PrintWorkflowSubmittedStatus.Failed, GetPrinterName(sender));
                }
            });

            if (!handled)
            {
                AppendDiagnostic(
                    "Virtual printer job failed",
                    GetPrinterName(sender),
                    "Background handler was already busy.");
                TryCompleteJob(args, PrintWorkflowSubmittedStatus.Failed, GetPrinterName(sender));
            }
        }
        finally
        {
            state.CompleteWhenIdle();
        }
    }

    private static async Task ProcessJobAsync(
        PrintWorkflowVirtualPrinterSession session,
        PrintWorkflowVirtualPrinterDataAvailableEventArgs args)
    {
        Uri printerUri = session.Printer.PrinterUri;
        string printerName = session.Printer.PrinterName;
        AppendDiagnostic(
            "Virtual printer data received",
            printerName,
            $"uri={printerUri}; contentType={args.SourceContent.ContentType}");

        if (!EndpointCatalog.TryResolve(printerUri, out VirtualEndpoint? endpoint) || endpoint is null)
        {
            AppendDiagnostic(
                "Virtual printer endpoint unresolved",
                printerName,
                $"uri={printerUri}; contentType={args.SourceContent.ContentType}");
            TryCompleteJob(args, PrintWorkflowSubmittedStatus.Failed, printerName);
            return;
        }

        LocalSettingsStore settingsStore = PackagedSettingsStoreFactory.Create();
        using LocalDiagnosticEventStore diagnosticEventStore = PackagedSettingsStoreFactory.CreateDiagnosticEventStore();
        JobUiCompletionResult uiCompletion = await CompleteJobUiAsync(args, settingsStore, endpoint, diagnosticEventStore)
            .ConfigureAwait(false);
        if (!uiCompletion.ShouldProcess)
        {
            return;
        }

        JobProcessingOptions? jobProcessingOptions = uiCompletion.UsedForegroundUi
            ? await settingsStore.ConsumeJobProcessingOptionsAsync().ConfigureAwait(false)
            : null;
        Lazy<Windows.Graphics.Printing.PrintTicket.WorkflowPrintTicket> printTicket =
            new(args.GetJobPrintTicket, LazyThreadSafetyMode.ExecutionAndPublication);
        VirtualPrinterJobProcessor processor = CreateProcessor(
            args,
            printTicket,
            settingsStore,
            jobProcessingOptions,
            diagnosticEventStore);
        using WinRtVirtualPrinterJob job = new(args, endpoint, () => printTicket.Value);
        await processor.ProcessAsync(job).ConfigureAwait(false);
    }

    private static async Task<JobUiCompletionResult> CompleteJobUiAsync(
        PrintWorkflowVirtualPrinterDataAvailableEventArgs args,
        LocalSettingsStore settingsStore,
        VirtualEndpoint endpoint,
        LocalDiagnosticEventStore diagnosticEventStore)
    {
        // Clipboard printing should finish directly from the source application's print dialog.
        if (endpoint.Kind == EndpointKind.Clipboard)
        {
            return new JobUiCompletionResult(true, false);
        }

        JobUiOptions options = await settingsStore.GetJobUiOptionsAsync().ConfigureAwait(false);
        if (!options.LaunchJobUi)
        {
            return new JobUiCompletionResult(true, false);
        }

        if (!args.UILauncher.IsUILaunchEnabled())
        {
            return new JobUiCompletionResult(true, false);
        }

        PrintWorkflowUICompletionStatus uiStatus = await args.UILauncher.LaunchAndCompleteUIAsync()
            .AsTask()
            .ConfigureAwait(false);
        if (uiStatus == PrintWorkflowUICompletionStatus.Completed)
        {
            return new JobUiCompletionResult(true, true);
        }

        await settingsStore.ConsumeJobProcessingOptionsAsync().ConfigureAwait(false);
        if (uiStatus == PrintWorkflowUICompletionStatus.UserCanceled)
        {
            await diagnosticEventStore
                .AppendAsync(
                    new DiagnosticEventRecord(
                        DateTimeOffset.UtcNow,
                        DiagnosticEventSeverity.Warning,
                        nameof(VirtualPrinterBackgroundTask),
                        "Job canceled",
                        endpoint.QueueName,
                        "User canceled from Job UI."))
                .ConfigureAwait(false);
            TryCompleteJob(args, PrintWorkflowSubmittedStatus.Canceled, endpoint.QueueName);
        }
        else
        {
            await diagnosticEventStore
                .AppendAsync(
                    new DiagnosticEventRecord(
                        DateTimeOffset.UtcNow,
                        DiagnosticEventSeverity.Error,
                        nameof(VirtualPrinterBackgroundTask),
                        "Job failed",
                        endpoint.QueueName,
                        $"Job UI completed with {uiStatus}."))
                .ConfigureAwait(false);
            TryCompleteJob(args, PrintWorkflowSubmittedStatus.Failed, endpoint.QueueName);
        }

        return new JobUiCompletionResult(false, true);
    }

    private static VirtualPrinterJobProcessor CreateProcessor(
        PrintWorkflowVirtualPrinterDataAvailableEventArgs args,
        Lazy<Windows.Graphics.Printing.PrintTicket.WorkflowPrintTicket> printTicket,
        LocalSettingsStore settingsStore,
        JobProcessingOptions? jobProcessingOptions,
        LocalDiagnosticEventStore diagnosticEventStore)
    {
        EndpointSinkResolver sinkResolver = new(new Dictionary<EndpointKind, ISink>
        {
            [EndpointKind.Pdf] = new TargetStreamSink(),
            [EndpointKind.Xps] = new TargetStreamSink(),
            [EndpointKind.PostScript] = new TargetStreamSink(),
            [EndpointKind.Cloud] = new CloudSink(PersistCloudSinkAsync),
            [EndpointKind.PwgRaster] = new TargetStreamSink(),
            [EndpointKind.Pclm] = new TargetStreamSink(),
            [EndpointKind.Clipboard] = new ClipboardSink(),
        });

        return new VirtualPrinterJobProcessor(
            new PdlRouter(),
            new WinRtPdlConverter(args, () => printTicket.Value),
            sinkResolver,
            settingsStore,
            jobProcessingOptions,
            new XpsWatermarkPdlTransformer(new ProjectedXpsWatermarker()),
            diagnosticEventStore);
    }

    private static async Task PersistCloudSinkAsync(
        Stream pdl,
        SinkWriteContext context,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(PackagedSettingsStoreFactory.GetRootDirectory(), "CloudSink");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(
            directory,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{GetSinkArtifactExtension(context.ContentType)}");
        FileStream output = File.Create(path);
        long bytes;
        await using (output.ConfigureAwait(false))
        {
            await pdl.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            bytes = output.Length;
        }

        if (bytes == 0)
        {
            throw new InvalidOperationException("The cloud sink received an empty PDL stream.");
        }

        using LocalDiagnosticEventStore diagnosticEventStore = PackagedSettingsStoreFactory.CreateDiagnosticEventStore();
        await diagnosticEventStore
            .AppendAsync(
                new DiagnosticEventRecord(
                    DateTimeOffset.UtcNow,
                    DiagnosticEventSeverity.Information,
                    nameof(VirtualPrinterBackgroundTask),
                    "Cloud sink artifact written",
                    context.Endpoint.QueueName,
                    $"path={path}; bytes={bytes}; contentType={context.ContentType}"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetSinkArtifactExtension(string contentType)
    {
        return PdlFormatInfo.TryParseContentType(contentType, out PdlFormat format)
            ? format switch
            {
                PdlFormat.Pdf => ".pdf",
                PdlFormat.Oxps => ".oxps",
                PdlFormat.Xps => ".xps",
                PdlFormat.PostScript => ".ps",
                PdlFormat.PwgRaster => ".pwgr",
                PdlFormat.Pclm => ".pclm",
                _ => ".pdl",
            }
            : ".pdl";
    }

    private static string GetPrinterName(PrintWorkflowVirtualPrinterSession session)
    {
        try
        {
            return session.Printer.PrinterName;
        }
        catch (Exception ex) when (BackgroundTaskExceptionPolicy.IsRecoverable(ex))
        {
            return string.Empty;
        }
    }

    private static void AppendDiagnostic(string message, string endpoint, string detail)
    {
        try
        {
            using CancellationTokenSource cancellation = new(DiagnosticWriteTimeout);
            using LocalDiagnosticEventStore diagnosticEventStore = PackagedSettingsStoreFactory.CreateDiagnosticEventStore();
            diagnosticEventStore
                .AppendAsync(
                    new DiagnosticEventRecord(
                        DateTimeOffset.UtcNow,
                        DiagnosticEventSeverity.Information,
                        nameof(VirtualPrinterBackgroundTask),
                        message,
                        endpoint,
                        detail),
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (BackgroundTaskExceptionPolicy.IsRecoverable(ex))
        {
            // Diagnostics must not make the virtual-printer contract fail.
        }
    }

    private static void TryCompleteJob(
        PrintWorkflowVirtualPrinterDataAvailableEventArgs args,
        PrintWorkflowSubmittedStatus status,
        string printerName)
    {
        try
        {
            args.CompleteJob(status);
        }
        catch (Exception ex) when (BackgroundTaskExceptionPolicy.IsRecoverable(ex))
        {
            AppendDiagnostic(
                "Virtual printer completion ignored",
                printerName,
                $"{status}: {ex}");
        }
    }

}
