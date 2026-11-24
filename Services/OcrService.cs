using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Nightforge.Services;

/// <summary>一行识别出来的文字。坐标是相对客户区的百分比，和窗口大小无关。</summary>
public sealed record TextLine(string Text, double Left, double Top, double Width, double Height)
{
    public double CenterX => Left + Width / 2;

    public double CenterY => Top + Height / 2;
}

/// <summary>
/// 系统自带 OCR（Windows.Media.Ocr）的薄封装：把抓到的画面认成一行行文字，
/// 再提供「模糊找字」——游戏 UI 的艺术字常被认差一两个字，所以不能要求完全相等。
/// </summary>
public static class OcrService
{
    private static OcrEngine? _engine;
    private static bool _tried;
    private static string _unavailableReason = "";

    /// <summary>本机能不能用 OCR；不能用时 <see cref="UnavailableReason"/> 说明原因。</summary>
    public static bool IsAvailable => TryEngine() is not null;

    public static string UnavailableReason => _unavailableReason;

    private static OcrEngine? TryEngine()
    {
        if (_tried)
        {
            return _engine;
        }

        _tried = true;
        try
        {
            // 优先中文；系统没装中文 OCR 语言包时退回用户语言列表
            _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
                      ?? OcrEngine.TryCreateFromUserProfileLanguages();

            if (_engine is null)
            {
                string langs = string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
                _unavailableReason = langs.Length == 0
                    ? "系统没有安装任何 OCR 语言包（设置 → 时间和语言 → 语言 → 添加「中文(简体)」的OCR组件）"
                    : $"系统 OCR 没有中文引擎（当前可用：{langs}）";
            }
        }
        catch (Exception ex)
        {
            _engine = null;
            _unavailableReason = $"OCR 初始化失败：{ex.Message}";
        }

        return _engine;
    }

    /// <summary>
    /// 识别一帧画面。
    ///
    /// 默认只跑原图（约 0.3 秒），够认账号、服务器名、角色名这些浅底深字。
    /// 传 thorough = true 时再补一条「放大两倍 + 反色」通道（合计约 0.8 秒），
    /// 那条通道是认深底白字按钮（如「进入游戏」）的唯一办法。
    /// 调用方先用快的找，找不到再补一次慢的，避免每轮都付全价。
    /// </summary>
    public static async Task<List<TextLine>> RecognizeAsync(ScreenCapturer.Frame frame, bool thorough = false)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return [];
        }

        List<TextLine> primary = await RecognizeCoreAsync(frame.Pixels, frame.Width, frame.Height)
            .ConfigureAwait(false);

        if (!thorough || TryEngine() is null)
        {
            return primary;
        }

        byte[] enlarged = InvertAndScale2X(frame.Pixels, frame.Width, frame.Height, out int width, out int height);
        List<TextLine> secondary = await RecognizeCoreAsync(enlarged, width, height).ConfigureAwait(false);

        if (secondary.Count == 0)
        {
            return primary;
        }

        var merged = new List<TextLine>(primary);
        foreach (TextLine line in secondary)
        {
            bool duplicate = false;
            foreach (TextLine existing in merged)
            {
                if (Math.Abs(existing.CenterX - line.CenterX) < 2.5
                    && Math.Abs(existing.CenterY - line.CenterY) < 2.5
                    && Similarity(Normalize(existing.Text), Normalize(line.Text)) > 0.7)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                merged.Add(line);
            }
        }

        return merged;
    }

    private static async Task<List<TextLine>> RecognizeCoreAsync(byte[] pixels, int width, int height)
    {
        var lines = new List<TextLine>();
        OcrEngine? engine = TryEngine();
        if (engine is null)
        {
            return lines;
        }

        using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            // 抓图里的 alpha 通道不可信，直接忽略，否则会被当成全透明
            BitmapAlphaMode.Ignore);

        OcrResult result = await engine.RecognizeAsync(bitmap);

        foreach (OcrLine line in result.Lines)
        {
            double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
            foreach (OcrWord word in line.Words)
            {
                left = Math.Min(left, word.BoundingRect.X);
                top = Math.Min(top, word.BoundingRect.Y);
                right = Math.Max(right, word.BoundingRect.X + word.BoundingRect.Width);
                bottom = Math.Max(bottom, word.BoundingRect.Y + word.BoundingRect.Height);
            }

            if (right <= left || bottom <= top)
            {
                continue;
            }

            lines.Add(new TextLine(
                line.Text,
                left * 100 / width,
                top * 100 / height,
                (right - left) * 100 / width,
                (bottom - top) * 100 / height));
        }

        return lines;
    }

    /// <summary>
    /// 放大两倍（Lanczos3）后反色。
    ///
    /// 这里的插值方式不能随便换。同一张游戏画面实测（单通道、逐张比对）：
    ///   原图              → 认不出「进入游戏」
    ///   1x 反色           → 认不出
    ///   2x 最近邻 + 反色  → 认不出（整行漏掉）
    ///   2x 双线性 + 反色  → 认不出
    ///   2x Lanczos + 反色 → 认出来了（50.0% , 58.6%）
    /// 深底白字的按钮在原图上边缘对比不够，只有高质量插值放大才稳。
    /// 坐标按百分比换算，放大倍数不影响点击位置。
    /// </summary>
    public static byte[] InvertAndScale2X(byte[] source, int width, int height, out int outWidth, out int outHeight)
    {
        outWidth = width * 2;
        outHeight = height * 2;

        // 先把两遍要用的权重算好。Lanczos 每个像素要算 7 次 sin，
        // 逐像素现算的话一张 1135×851 要 1.5 秒；预计算后所有行共用同一组权重。
        (int Start, double[] Weights)[] horizontalWeights = BuildWeights(outWidth, width);
        (int Start, double[] Weights)[] verticalWeights = BuildWeights(outHeight, height);

        // 第一遍：水平放大（顺便反色）
        var horizontal = new byte[outWidth * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * width * 4;
            int targetRow = y * outWidth * 4;

            for (int x = 0; x < outWidth; x++)
            {
                (int start, double[] weights) = horizontalWeights[x];
                double b = 0, g = 0, r = 0;

                for (int k = 0; k < weights.Length; k++)
                {
                    int idx = sourceRow + Math.Clamp(start + k, 0, width - 1) * 4;
                    b += (255 - source[idx]) * weights[k];
                    g += (255 - source[idx + 1]) * weights[k];
                    r += (255 - source[idx + 2]) * weights[k];
                }

                int target = targetRow + x * 4;
                horizontal[target] = Clamp(b);
                horizontal[target + 1] = Clamp(g);
                horizontal[target + 2] = Clamp(r);
                horizontal[target + 3] = 255;
            }
        }

        // 第二遍：垂直放大
        var result = new byte[outWidth * outHeight * 4];
        for (int y = 0; y < outHeight; y++)
        {
            (int start, double[] weights) = verticalWeights[y];
            int targetRow = y * outWidth * 4;

            for (int x = 0; x < outWidth; x++)
            {
                double b = 0, g = 0, r = 0;

                for (int k = 0; k < weights.Length; k++)
                {
                    int idx = (Math.Clamp(start + k, 0, height - 1) * outWidth + x) * 4;
                    b += horizontal[idx] * weights[k];
                    g += horizontal[idx + 1] * weights[k];
                    r += horizontal[idx + 2] * weights[k];
                }

                int target = targetRow + x * 4;
                result[target] = Clamp(b);
                result[target + 1] = Clamp(g);
                result[target + 2] = Clamp(r);
                result[target + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>为放大两倍的每个目标像素算好取值起点与归一化后的权重。</summary>
    private static (int Start, double[] Weights)[] BuildWeights(int outLength, int sourceLength)
    {
        var table = new (int Start, double[] Weights)[outLength];

        for (int i = 0; i < outLength; i++)
        {
            double s = (i + 0.5) / 2.0 - 0.5;
            int from = (int)Math.Floor(s) - 2;
            int to = (int)Math.Ceiling(s) + 2;

            var weights = new double[to - from + 1];
            double sum = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                weights[k] = Lanczos(s - (from + k));
                sum += weights[k];
            }

            if (sum != 0)
            {
                for (int k = 0; k < weights.Length; k++)
                {
                    weights[k] /= sum;
                }
            }

            table[i] = (from, weights);
        }

        return table;
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    /// <summary>Lanczos 核（a = 3）。</summary>
    private static double Lanczos(double x)
    {
        const int a = 3;
        double ax = Math.Abs(x);

        if (ax < 1e-8)
        {
            return 1;
        }

        if (ax >= a)
        {
            return 0;
        }

        double px = Math.PI * x;
        return a * Math.Sin(px) * Math.Sin(px / a) / (px * px);
    }

    /// <summary>把文字压成「只留汉字和字母数字」，这样「红 颜 知己」「0红颜知己」都能对上。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 目标文字与识别结果有多像（0–1）。包含关系直接给满分，
    /// 否则用最长公共子序列比例，容忍艺术字被认错一两个字。
    /// </summary>
    public static double Similarity(string normalizedLine, string normalizedTarget)
    {
        if (normalizedTarget.Length == 0 || normalizedLine.Length == 0)
        {
            return 0;
        }

        if (normalizedLine.Contains(normalizedTarget, StringComparison.Ordinal))
        {
            return 1;
        }

        // 服务器名常常带个前缀小圆点，去掉前两位再比一次
        int lcs = LcsLength(normalizedLine, normalizedTarget);
        return (double)lcs / normalizedTarget.Length;
    }

    private static int LcsLength(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                current[j] = a[i - 1] == b[j - 1]
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[b.Length];
    }
}
