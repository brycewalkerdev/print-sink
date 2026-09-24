using PrintSink.Core.Endpoints;
using Windows.ApplicationModel;
using Windows.Storage;

namespace PrintSink.Tasks;

/// <summary>Delegates clipboard access to the interactive desktop process.</summary>
internal sealed class ClipboardSink : ISink
{
    public async Task WriteAsync(Stream pdl, SinkWriteContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdl);
        string directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "ClipboardJobs");
        Directory.CreateDirectory(directory);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, id);
        try
        {
            using (FileStream output = File.Create(path + ".pwgr"))
            {
                await pdl.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            _ = await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppWithArgumentsAsync(
                "--copy-print-job " + id).AsTask(cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (!File.Exists(path + ".result"))
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }

            string result = await File.ReadAllTextAsync(path + ".result", cancellationToken).ConfigureAwait(false);
            if (result != "OK")
            {
                throw new InvalidOperationException("The desktop clipboard writer failed: " + result);
            }
        }
        finally
        {
            File.Delete(path + ".pwgr");
            File.Delete(path + ".result");
            File.Delete(path + ".result.tmp");
        }
    }
}
