namespace Nightforge.Services;

/// <summary>一个窗口的目标位置与大小。坐标为物理像素，尺寸指「客户区」（不含标题栏与边框）。</summary>
public readonly record struct WindowSlot(int X, int Y, int Width, int Height);

/// <summary>一次排列的完整结果：每个窗口的槽位 + 行列数 + 统一的窗口尺寸，便于写日志与做校验。</summary>
public sealed record LayoutPlan(List<WindowSlot> Slots, int Columns, int Rows, int WindowWidth, int WindowHeight)
{
    public bool IsEmpty => Slots.Count == 0;
}

/// <summary>
/// 窗口排列计算。只做算术，不碰系统 API，规则可以单独推演、也方便日后加新排法。
/// </summary>
public static class LayoutCalculator
{
    /// <summary>低于这个尺寸基本上没法正常看游戏界面，自动适配时这类方案直接淘汰。</summary>
    public const int MinWindowWidth = 240;

    public const int MinWindowHeight = 180;

    /// <summary>间距上限，防止手输 9999 之后排出诡异结果。</summary>
    public const int MaxGap = 60;

    /// <summary>
    /// 自动适配：在给定工作区内，为 <paramref name="count"/> 个窗口挑一个
    /// 「互不重叠、整齐居中、且窗口客户区刚好等于画面比例」的方案。
    /// <para>
    /// 做法是枚举所有能装下的列数 × 行数组合，每种组合下按比例算出该格子里能放的最大窗口，
    /// 取面积最大的那个组合（面积相同时优先选整体轮廓更接近屏幕比例的，视觉上更匀）。
    /// </para>
    /// </summary>
    public static LayoutPlan AutoFit(
        int count,
        int left,
        int top,
        int screenWidth,
        int screenHeight,
        int ratioWidth,
        int ratioHeight,
        int gap)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || ratioWidth <= 0 || ratioHeight <= 0)
        {
            return Empty;
        }

        count = Math.Clamp(count, 1, 5);
        gap = Math.Clamp(gap, 0, MaxGap);

        bool found = false;
        int bestCols = 1, bestRows = 1, bestW = 0, bestH = 0;
        double bestArea = -1;

        foreach ((int cols, int rows) in CandidateGrids(count))
        {
            int cellW = (screenWidth - (cols + 1) * gap) / cols;
            int cellH = (screenHeight - (rows + 1) * gap) / rows;
            if (cellW <= 0 || cellH <= 0)
            {
                continue;
            }

            // 在格子里放一个该比例的最大矩形
            double scale = Math.Min((double)cellW / ratioWidth, (double)cellH / ratioHeight);
            int w = (int)Math.Floor(ratioWidth * scale);
            int h = (int)Math.Floor(ratioHeight * scale);
            if (w < MinWindowWidth || h < MinWindowHeight)
            {
                continue;
            }

            double area = (double)w * h;

            // 面积一样大时优先多分几列：宽屏上「左右并排」比「上下叠」更像正常的排列
            bool better = !found || area > bestArea + 0.5 || (Math.Abs(area - bestArea) <= 0.5 && cols > bestCols);

            if (better)
            {
                found = true;
                bestCols = cols;
                bestRows = rows;
                bestW = w;
                bestH = h;
                bestArea = area;
            }
        }

        if (!found)
        {
            // 工作区实在太小或间距太大，退化成最小可用尺寸，至少保证不重叠
            return Fixed(count, left, top, screenWidth, screenHeight, MinWindowWidth, MinWindowHeight, gap);
        }

        return Build(count, left, top, screenWidth, screenHeight, bestCols, bestRows, bestW, bestH, gap);
    }

    /// <summary>
    /// 固定尺寸：按用户指定的宽高排布。
    /// 5 个窗口仍走五点布局（左上 / 上中 / 右上 / 左下 / 右下），其余数量按能装下的列数自动分行。
    /// 尺寸过大导致压窗口时不做缩放，交给调用方在日志里提示。
    /// </summary>
    public static LayoutPlan Fixed(
        int count,
        int left,
        int top,
        int screenWidth,
        int screenHeight,
        int width,
        int height,
        int gap)
    {
        if (count <= 0 || width <= 0 || height <= 0)
        {
            return Empty;
        }

        count = Math.Clamp(count, 1, 5);
        gap = Math.Clamp(gap, 0, MaxGap);

        var slots = new List<WindowSlot>(count);

        if (count == 5)
        {
            int xLeft = left + gap;
            int xRight = Math.Max(xLeft, left + screenWidth - width - gap);
            int xCenter = left + (screenWidth - width) / 2;
            int yTop = top + gap;
            int yBottom = Math.Max(yTop, top + screenHeight - height - gap);

            slots.Add(new WindowSlot(xLeft, yTop, width, height));
            slots.Add(new WindowSlot(xCenter, yTop, width, height));
            slots.Add(new WindowSlot(xRight, yTop, width, height));
            slots.Add(new WindowSlot(xLeft, yBottom, width, height));
            slots.Add(new WindowSlot(xRight, yBottom, width, height));

            return new LayoutPlan(slots, 3, 2, width, height);
        }

        int cols = Math.Max(1, (screenWidth + gap) / (width + gap));
        cols = Math.Min(cols, count);
        int rows = (int)Math.Ceiling(count / (double)cols);

        int blockW = cols * width + (cols - 1) * gap;
        int blockH = rows * height + (rows - 1) * gap;
        int startX = left + Math.Max(gap, (screenWidth - blockW) / 2);
        int startY = top + Math.Max(gap, (screenHeight - blockH) / 2);

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            slots.Add(new WindowSlot(
                startX + col * (width + gap),
                startY + row * (height + gap),
                width,
                height));
        }

        CenterLoneBottom(slots, count, cols, rows, left, screenWidth, width);

        return new LayoutPlan(slots, cols, rows, width, height);
    }

    /// <summary>两个槽位是否互相压住。</summary>
    public static bool TryFindOverlap(IReadOnlyList<WindowSlot> slots, out WindowSlot first, out WindowSlot second)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            for (int j = i + 1; j < slots.Count; j++)
            {
                if (Intersects(slots[i], slots[j]))
                {
                    first = slots[i];
                    second = slots[j];
                    return true;
                }
            }
        }

        first = default;
        second = default;
        return false;
    }

    /// <summary>槽位是否跑到工作区之外。</summary>
    public static bool IsOutside(WindowSlot slot, int left, int top, int screenWidth, int screenHeight)
        => slot.X < left
        || slot.Y < top
        || slot.X + slot.Width > left + screenWidth
        || slot.Y + slot.Height > top + screenHeight;

    public static readonly LayoutPlan Empty = new([], 0, 0, 0, 0);

    /// <summary>
    /// 候选行列组合。
    /// 5 个窗口固定 3 列 × 2 行——下排左右各一个、中间留空，这是本程序一直以来的五点布局，
    /// 不随比例变化，保证「5 个窗口长什么样」是可预期的。
    /// </summary>
    private static IEnumerable<(int Cols, int Rows)> CandidateGrids(int count)
    {
        if (count == 5)
        {
            yield return (3, 2);
            yield break;
        }

        for (int cols = 1; cols <= count; cols++)
        {
            yield return (cols, (int)Math.Ceiling(count / (double)cols));
        }
    }

    private static LayoutPlan Build(
        int count,
        int left,
        int top,
        int screenWidth,
        int screenHeight,
        int cols,
        int rows,
        int width,
        int height,
        int gap)
    {
        int blockW = cols * width + (cols - 1) * gap;
        int blockH = rows * height + (rows - 1) * gap;
        int startX = left + Math.Max(gap, (screenWidth - blockW) / 2);
        int startY = top + Math.Max(gap, (screenHeight - blockH) / 2);

        var slots = new List<WindowSlot>(count);
        for (int i = 0; i < count; i++)
        {
            (int col, int row) = CellOf(i, count, cols);
            slots.Add(new WindowSlot(
                startX + col * (width + gap),
                startY + row * (height + gap),
                width,
                height));
        }

        CenterLoneBottom(slots, count, cols, rows, left, screenWidth, width);

        return new LayoutPlan(slots, cols, rows, width, height);
    }

    /// <summary>
    /// 3 个窗口排成 2 列 2 行时，第三个单独占下排左格，看着偏。
    /// 这里把它挪到下排正中，整体变成「上面两个、下面一个居中」的对称形状。
    /// </summary>
    private static void CenterLoneBottom(List<WindowSlot> slots, int count, int cols, int rows, int left, int screenWidth, int width)
    {
        if (count != 3 || cols != 2 || rows != 2 || slots.Count < 3)
        {
            return;
        }

        slots[2] = slots[2] with { X = left + (screenWidth - width) / 2 };
    }

    /// <summary>第 i 个窗口落在第几列第几行。5 个窗口时下排只占左右两格，中间留空。</summary>
    private static (int Col, int Row) CellOf(int index, int count, int cols)
    {
        if (count == 5 && cols == 3)
        {
            return index switch
            {
                0 => (0, 0),
                1 => (1, 0),
                2 => (2, 0),
                3 => (0, 1),
                _ => (2, 1)
            };
        }

        return (index % cols, index / cols);
    }

    private static bool Intersects(WindowSlot a, WindowSlot b)
        => a.X < b.X + b.Width
        && b.X < a.X + a.Width
        && a.Y < b.Y + b.Height
        && b.Y < a.Y + a.Height;
}
