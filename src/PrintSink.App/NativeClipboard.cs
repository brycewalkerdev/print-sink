using System.ComponentModel;
using System.Runtime.InteropServices;
using PrintSink.Core.Pdl;

namespace PrintSink.App;

/// <summary>Writes a persistent bitmap from the desktop process.</summary>
internal static partial class NativeClipboard
{
    internal static void WriteBitmap(PwgRasterPage page, CancellationToken cancellationToken)
    {
        // A real owner window is required by EmptyClipboard/SetClipboardData.
        // Eager CF_DIB data survives this process and needs no COM apartment.
        nint window = CreateWindowExW(0, "STATIC", "PrintSink clipboard", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        nint memory = 0;
        bool opened = false;
        try
        {
            byte[] pixels = page.Pixels.ToArray();
            memory = GlobalAlloc(0x0042, checked((nuint)(40 + pixels.Length)));
            if (memory == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nint pointer = GlobalLock(memory);
            if (pointer == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                Marshal.WriteInt32(pointer, 0, 40);
                Marshal.WriteInt32(pointer, 4, page.Width);
                Marshal.WriteInt32(pointer, 8, page.Height);
                Marshal.WriteInt16(pointer, 12, 1);
                Marshal.WriteInt16(pointer, 14, 32);
                Marshal.WriteInt32(pointer, 20, pixels.Length);
                for (int y = 0; y < page.Height; y++)
                {
                    Marshal.Copy(pixels, y * page.Stride, pointer + 40 + (page.Height - y - 1) * page.Stride, page.Stride);
                }
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            for (int attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OpenClipboard(window) != 0)
                {
                    opened = true;
                    break;
                }

                Thread.Sleep(50);
            }

            if (!opened || EmptyClipboard() == 0 || SetClipboardData(8, memory) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            memory = 0; // Ownership transferred to Windows.
        }
        finally
        {
            if (opened)
            {
                _ = CloseClipboard();
            }

            if (memory != 0)
            {
                _ = GlobalFree(memory);
            }

            _ = DestroyWindow(window);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int DestroyWindow(nint window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int OpenClipboard(nint window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int EmptyClipboard();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int CloseClipboard();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GlobalUnlock(nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);
}
