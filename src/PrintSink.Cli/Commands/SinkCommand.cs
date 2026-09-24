using PrintSink.Core.Endpoints;
using PrintSink.Core.Pdl;
using PrintSink.Core.Processing;
using System.CommandLine;

namespace PrintSink.Cli.Commands;

/// <summary>
/// Creates sink test commands.
/// </summary>
internal static class SinkCommand
{
    /// <summary>
    /// Creates the sink command.
    /// </summary>
    /// <param name="context">The CLI context.</param>
    /// <returns>The configured command.</returns>
    public static Command Create(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Command command = new("sink", "Exercise sink routing without print activation.");
        command.Subcommands.Add(CreateTestCommand(context));

        return command;
    }

    private static Command CreateTestCommand(CliContext context)
    {
        Option<string> endpointOption = new("--endpoint", "-e")
        {
            Description = "Endpoint kind: pdf, xps, postscript, cloud, pwg-raster, or pclm.",
            Required = true,
        };
        Option<string> contentTypeOption = new("--content-type", "-c")
        {
            Description = "Source PDL content type.",
            Required = true,
        };
        Option<string?> inputOption = new("--input", "-i")
        {
            Description = "Optional fixture PDL file path.",
        };
        Option<string?> outputOption = new("--output", "-o")
        {
            Description = "Optional output path for file-backed endpoints.",
        };

        Command command = new("test", "Resolve a fixture PDL stream through a PrintSink endpoint.");
        command.Options.Add(endpointOption);
        command.Options.Add(contentTypeOption);
        command.Options.Add(inputOption);
        command.Options.Add(outputOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string endpointText = parseResult.GetRequiredValue(endpointOption);
            string contentType = parseResult.GetRequiredValue(contentTypeOption);
            string? inputPath = parseResult.GetValue(inputOption);
            string? outputPath = parseResult.GetValue(outputOption);

            if (!EndpointParser.TryParse(endpointText, out EndpointKind endpointKind))
            {
                await context.Error.WriteLineAsync($"Unknown endpoint '{endpointText}'.").ConfigureAwait(false);
                return CliExitCodes.UsageError;
            }

            if (!string.IsNullOrWhiteSpace(inputPath) && !File.Exists(inputPath))
            {
                await context.Error.WriteLineAsync($"Input file not found: {inputPath}").ConfigureAwait(false);
                return CliExitCodes.ValidationFailed;
            }

            VirtualEndpoint endpoint = EndpointCatalog.GetByKind(endpointKind);
            if (!endpoint.RequiresTargetFile && !string.IsNullOrWhiteSpace(outputPath))
            {
                await context.Error.WriteLineAsync($"Endpoint '{endpoint.QueueName}' is not file-backed and does not accept --output.").ConfigureAwait(false);
                return CliExitCodes.ValidationFailed;
            }

            CapturingSink cloudSink = new();
            ISink fileSink = new TargetStreamSink();
            EndpointSinkResolver sinkResolver = new(new Dictionary<EndpointKind, ISink>
            {
                [EndpointKind.Pdf] = fileSink,
                [EndpointKind.Xps] = fileSink,
                [EndpointKind.PostScript] = fileSink,
                [EndpointKind.PwgRaster] = fileSink,
                [EndpointKind.Pclm] = fileSink,
                [EndpointKind.Clipboard] = new CapturingSink(),
                [EndpointKind.Cloud] = cloudSink,
            });
            FixtureVirtualPrinterJob job = new(contentType, endpoint, inputPath, outputPath);
            VirtualPrinterJobProcessor processor = new(new PdlRouter(), new FixturePdlConverter(), sinkResolver);
            VirtualPrinterJobResult result = await processor.ProcessAsync(job, cancellationToken).ConfigureAwait(false);
            PdlPlan plan = result.Plan;

            await context.Output.WriteLineAsync($"Endpoint: {endpoint.QueueName}").ConfigureAwait(false);
            await context.Output.WriteLineAsync($"Source: {plan.SourceFormat?.ToString() ?? "Unknown"}").ConfigureAwait(false);
            await context.Output.WriteLineAsync($"Target: {plan.TargetFormat}").ConfigureAwait(false);
            await context.Output.WriteLineAsync($"Action: {plan.ActionKind}").ConfigureAwait(false);
            await context.Output.WriteLineAsync($"Conversion: {plan.ConversionKind?.ToString() ?? "None"}").ConfigureAwait(false);
            await context.Output.WriteLineAsync($"Reason: {plan.Reason}").ConfigureAwait(false);
            await context.Output.WriteLineAsync($"Status: {result.Status}").ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(inputPath))
            {
                await context.Output.WriteLineAsync($"InputBytes: {new FileInfo(inputPath).Length}").ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                await context.Output.WriteLineAsync($"Output: {outputPath}").ConfigureAwait(false);
            }

            long outputBytes = endpoint.Kind == EndpointKind.Cloud
                ? cloudSink.BytesWritten
                : job.OutputBytes;
            await context.Output.WriteLineAsync($"OutputBytes: {outputBytes}").ConfigureAwait(false);
            job.DeleteTemporaryOutput();

            return result.Status == Core.Abstractions.VirtualPrinterJobStatus.Succeeded
                ? CliExitCodes.Success
                : CliExitCodes.ValidationFailed;
        });

        return command;
    }
}
