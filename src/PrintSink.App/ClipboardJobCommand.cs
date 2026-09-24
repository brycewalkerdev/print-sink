using PrintSink.Core.Pdl;
using Windows.Storage;

namespace PrintSink.App;

/// <summary>Completes clipboard jobs in the interactive desktop process.</summary>
internal static class ClipboardJobCommand
{
    internal static async Task<int?> RunIfRequestedAsync(string[] args)
    {
        int commandIndex = Array.IndexOf(args, "--copy-print-job");
        if (commandIndex < 0)
        {
            return null;
        }

        if (commandIndex + 1 >= args.Length || !Guid.TryParseExact(args[commandIndex + 1], "N", out Guid jobId))
        {
            return 1;
        }

        string directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "ClipboardJobs");
        string path = Path.Combine(directory, jobId.ToString("N"));
        string result;
        int exitCode;
        try
        {
            using FileStream input = File.OpenRead(path + ".pwgr");
            PwgRasterPage page = PwgRasterReader.ReadStackedPages(input);
            NativeClipboard.WriteBitmap(page, CancellationToken.None);
            result = "OK";
            exitCode = 0;
        }
        catch (Exception ex) when (AppExceptionPolicy.IsRecoverable(ex))
        {
            result = ex.ToString();
            exitCode = 1;
        }

        await File.WriteAllTextAsync(path + ".result.tmp", result).ConfigureAwait(false);
        File.Move(path + ".result.tmp", path + ".result", true);
        return exitCode;
    }
}
