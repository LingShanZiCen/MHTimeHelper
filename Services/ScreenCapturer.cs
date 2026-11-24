using System.IO;
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

    public static Frame? Capture(IntPtr hwnd) => Capture(hwnd, out _);

    /// <summary>
    /// 抓图，并且保证这一帧确实是这个窗口自己的画面。
    ///
    /// 游戏的 DirectX 画面 PrintWindow 抓出来是全黑，只能退回「从屏幕拷」；
    /// 而屏幕拷贝只忠实于屏幕——游戏窗口要是被别的窗口盖住，拷回来的就是那个窗口，
    /// 于是认字会认出一堆无关内容、判不清当前在哪一屏。所以拷之前必须确认窗口真的在最前面，
    /// 抢不到就返回 null 并说明原因，让上层报错，而不是拿一张假画面去猜。
    /// </summary>
    public static Frame? Capture(IntPtr hwnd, out string failureReason)
    {
        failureReason = string.Empty;

        if (!NativeMethods.IsWindow(hwnd))
        {
            failureReason = "窗口已经不存在";
            return null;
        }

        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rc))
        {
            failureReason = "读不到窗口客户区（窗口可能已最小化）";
            return null;
        }

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000)
        {
            failureReason = $"窗口客户区尺寸异常（{width}×{height}），可能已最小化";
            return null;
        }

        // 第一条路：PrintWindow 不依赖前台，能抓到被遮挡的窗口（DirectX 独占渲染时多为全黑）
        Frame? printed = CaptureWithPrintWindow(hwnd, width, height);
        if (printed is not null && !IsMostlyBlank(printed.Value))
        {
            return printed;
        }

        // 第二条路：从屏幕拷，前提是窗口真的在最前面
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!WindowFocus.Force(hwnd))
            {
                failureReason = "游戏窗口抢不到前台（被别的窗口挡住，或焦点被切走了）";
                continue;
            }

            Thread.Sleep(140);

            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                failureReason = "游戏窗口刚抢到前台又被切走";
                continue;
            }

            Frame? fromScreen = CaptureFromScreen(hwnd, width, height);
            if (fromScreen is null || IsMostlyBlank(fromScreen.Value))
            {
                failureReason = "屏幕拷贝拿不到画面（窗口最小化或画面全黑）";
                continue;
            }

            // 拷的这一刻窗口被切走的话，拿到的可能是别的程序的画面，宁可不认
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                failureReason = "抓图过程中游戏窗口被切走，画面不可信";
                continue;
            }

            failureReason = string.Empty;
            return fromScreen;
        }

        return null;
    }

    /// <summary>
    /// 把一帧存成 24 位 BMP。只用于排查「这一屏认不出来」，不参与正常登录流程。
    /// </summary>
    public static string? SaveBmp(Frame frame, string path)
    {
        try
        {
            int rowBytes = frame.Width * 3;
            int padding = (4 - rowBytes % 4) % 4;
            int rowSize = rowBytes + padding;
            int dataSize = rowSize * frame.Height;

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(14 + 40 + dataSize);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write((short)1);
            writer.Write((short)24);
            writer.Write(0);
            writer.Write(dataSize);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);

            byte[] pad = new byte[padding];
            byte[] row = new byte[rowBytes];

            // BMP 是自下而上存的，而 Pixels 第 0 行在最上面，所以倒着写
            for (int y = frame.Height - 1; y >= 0; y--)
            {
                int offset = y * frame.Width * 4;
                for (int x = 0; x < frame.Width; x++)
                {
                    row[x * 3] = frame.Pixels[offset + x * 4];
                    row[x * 3 + 1] = frame.Pixels[offset + x * 4 + 1];
                    row[x * 3 + 2] = frame.Pixels[offset + x * 4 + 2];
                }

                writer.Write(row);
                if (padding > 0)
                {
                    writer.Write(pad);
                }
            }

            return path;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// 从屏幕拷一块矩形（物理像素），拷之前先把指定窗口提到前台。
    ///
    /// 用途：账号历史列表有时是贴在悬浮窗下面的独立小弹窗，只截窗口本身看不到它，
    /// 所以按「悬浮窗 + 下面多留一截」的区域从屏幕拷。坐标原点就是这块区域的左上角。
    /// </summary>
    public static Frame? CaptureScreenRect(IntPtr windowToRaise, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000)
        {
            return null;
        }

        if (windowToRaise != IntPtr.Zero)
        {
            if (!WindowFocus.Force(windowToRaise))
            {
                return null;
            }

            Thread.Sleep(140);

            if (NativeMethods.GetForegroundWindow() != windowToRaise)
            {
                return null;
            }
        }

        return CaptureScreen(x, y, width, height);
    }

    /// <summary>兜底：窗口提到前台后直接从屏幕拷贝客户区那一块。</summary>
    private static Frame? CaptureFromScreen(IntPtr hwnd, int width, int height)
    {
        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return null;
        }

        return CaptureScreen(origin.X, origin.Y, width, height);
    }

    private static Frame? CaptureScreen(int x, int y, int width, int height)
    {
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
            bool ok = NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y,
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
