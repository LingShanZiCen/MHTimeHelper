using System.Runtime.InteropServices;

namespace Nightforge.Services;

/// <summary>
/// 抓窗口客户区的画面，交给 OCR 认字。
///
/// 优先 PrintWindow（带 PW_RENDERFULLCONTENT，DirectX 渲染的窗口也能抓到，窗口被挡住也行），
/// 抓到全黑时退回「把窗口提到最前 + 从屏幕拷贝」，两条路都失败才算抓不到。
/// </summary>
public static class ScreenCapturer
{
    /// <summary>一帧画面：宽、高、BGRA 排列的像素（每像素 4 字节，第 0 行在最上面）。</summary>
    public readonly record struct Frame(int Width, int Height, byte[] Pixels);

    public static Frame? Capture(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rc))
        {
            return null;
        }

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000)
        {
            return null;
        }

        Frame? captured = CaptureWithPrintWindow(hwnd, width, height);
        if (captured is not null && !IsMostlyBlank(captured.Value))
        {
            return captured;
        }

        Frame? fromScreen = CaptureFromScreen(hwnd, width, height);
        if (fromScreen is not null && !IsMostlyBlank(fromScreen.Value))
        {
            return fromScreen;
        }

        return captured ?? fromScreen;
    }

    private static Frame? CaptureWithPrintWindow(IntPtr hwnd, int width, int height)
    {
        IntPtr windowDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            windowDc = NativeMethods.GetDC(hwnd);
            if (windowDc == IntPtr.Zero)
            {
                return null;
            }

            memoryDc = NativeMethods.CreateCompatibleDC(windowDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(windowDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            bool ok = NativeMethods.PrintWindow(hwnd, memoryDc,
                NativeMethods.PW_CLIENTONLY | NativeMethods.PW_RENDERFULLCONTENT);
            if (!ok)
            {
                return null;
            }

            return ReadPixels(memoryDc, bitmap, width, height);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (windowDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(hwnd, windowDc);
            }
        }
    }

    /// <summary>兜底：窗口提到前台后直接从屏幕拷贝客户区那一块。</summary>
    private static Frame? CaptureFromScreen(IntPtr hwnd, int width, int height)
    {
        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return null;
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            bool ok = NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, origin.X, origin.Y,
                NativeMethods.SRCCOPY);
            if (!ok)
            {
                return null;
            }

            return ReadPixels(memoryDc, bitmap, width, height);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>把位图读成 BGRA 字节数组（负高度 = 第 0 行在最上面，和 OCR 期望的顺序一致）。</summary>
    private static Frame? ReadPixels(IntPtr hdc, IntPtr bitmap, int width, int height)
    {
        var info = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
            biSizeImage = (uint)(width * height * 4)
        };

        var pixels = new byte[width * height * 4];
        int lines = NativeMethods.GetDIBits(hdc, bitmap, 0, (uint)height, pixels, ref info,
            NativeMethods.DIB_RGB_COLORS);

        return lines == 0 ? null : new Frame(width, height, pixels);
    }

    /// <summary>
    /// 全黑基本就是「没抓到」：DirectX 独占、窗口最小化、渲染还没出第一帧都会这样。
    /// 抽查一部分像素，超过 98% 是纯黑就判定为无效。
    /// </summary>
    private static bool IsMostlyBlank(Frame frame)
    {
        byte[] pixels = frame.Pixels;
        int total = pixels.Length / 4;
        if (total == 0)
        {
            return true;
        }

        int step = Math.Max(1, total / 4000);
        int sampled = 0;
        int black = 0;

        for (int i = 0; i < total; i += step)
        {
            int offset = i * 4;
            sampled++;

            if (pixels[offset] < 8 && pixels[offset + 1] < 8 && pixels[offset + 2] < 8)
            {
                black++;
            }
        }

        return sampled > 0 && black * 100L / sampled >= 98;
    }
}
