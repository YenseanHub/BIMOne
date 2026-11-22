using System.Text.Json;
using ACadSharp.IO;

namespace CadReader.Core;

/// <summary>图像交叉验证报告：用 DWG 内嵌预览图（AutoCAD 渲染）核对矢量解析结果。</summary>
public sealed class ImageVerificationReport
{
    public bool PreviewAvailable { get; set; }
    public string PreviewInfo { get; set; } = "";
    public double GlobalIoU { get; set; }
    public double PreviewCoverage { get; set; }   // 预览图线条被 IR 覆盖的比例
    public double IrPrecision { get; set; }       // IR 线条在预览图中有支撑的比例
    public List<LowConsistencyRegion> Regions { get; set; } = new();
    public string Verdict { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class LowConsistencyRegion
{
    public int Row { get; set; }
    public int Col { get; set; }
    public double IoU { get; set; }
    public string Hint { get; set; } = "";
}

/// <summary>
/// 图像交叉验证辅路。图像源 = DWG 内嵌缩略图（独立于矢量解析，不是自证循环）。
/// 做法：预览图与 IR 栅格各自归一化到同一网格 → 二值化 → 分区 IoU → 输出低一致区域。
/// 零新增 NuGet 依赖：自己解析 DIB + Bresenham 画线。
/// </summary>
public static class ImageVerifier
{
    private const int GridW = 180, GridH = 103; // 与真实样本预览图一致，其他图自动缩放
    private const byte Dark = 1, Blank = 0;

    public static ImageVerificationReport Verify(string cadPath, IrDocument ir)
    {
        var report = new ImageVerificationReport();
        if (!File.Exists(cadPath)) { report.Notes = "CAD 文件不存在"; return report; }

        byte[]? dib = null;
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"cadprev_{Guid.NewGuid():N}.bmp");
            new DwgReader(cadPath).ReadPreview()?.Save(tmp);
            if (File.Exists(tmp)) { dib = File.ReadAllBytes(tmp); File.Delete(tmp); }
        }
        catch (Exception ex) { report.Notes = $"读取预览图失败: {ex.Message}"; }

        if (dib is null)
        {
            report.Notes = "DWG 无内嵌预览图（DXF 也没有）；无法做图像交叉验证，请用 DXF 主路 + 人工复核";
            return report;
        }

        try
        {
            var preview = LoadDib(dib, GridW, GridH);
            report.PreviewAvailable = true;
            report.PreviewInfo = $"{preview.W}×{preview.H} 位图(1bpp 二值化)";

            var rendered = RasterizeIr(ir, GridW, GridH);
            Compare(preview.Pixels, rendered.Pixels, report);
            report.Verdict = report.GlobalIoU >= 0.35 ? "consistent"
                           : report.GlobalIoU >= 0.18 ? "needs_review" : "mismatch";
            if (!string.IsNullOrEmpty(report.Notes))
                report.Notes += "；";
            report.Notes += "低一致区域应优先做专家研判/人工复核（预览图分辨率有限，仅作定向线索，不作最终判据）";
        }
        catch (Exception ex)
        {
            report.Notes = $"图像比对失败: {ex.GetType().Name}: {ex.Message}";
        }
        return report;
    }

    public static string ToJson(ImageVerificationReport report) => JsonSerializer.Serialize(report,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ---------- DIB 读取（BITMAPINFOHEADER + 调色板，bottom-up） ----------
    private sealed class Bitmap { public int W, H; public byte[] Pixels = Array.Empty<byte>(); }

    private static Bitmap LoadDib(byte[] b, int targetW, int targetH)
    {
        // DwgPreview.Save 输出的是 DIB：偏移 0 = BITMAPINFOHEADER(40B)，无 BM 文件头
        int w = BitConverter.ToInt32(b, 4), h = BitConverter.ToInt32(b, 8);
        int bpp = BitConverter.ToUInt16(b, 14);
        if (bpp is not (1 or 4 or 8 or 24 or 32))
            throw new NotSupportedException($"不支持的位图色深: {bpp}");

        int paletteCount = bpp <= 8 ? 1 << bpp : 0;
        int dataOffset = 40 + paletteCount * 4;
        int stride = ((w * bpp + 31) / 32) * 4;

        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = dataOffset + (h - 1 - y) * stride; // bottom-up
            for (int x = 0; x < w; x++)
            {
                int idx = row + x * bpp / 8;
                if (idx >= b.Length) continue;
                int g;
                if (bpp == 8) g = GrayFromPalette(b, 40 + b[idx] * 4);
                else if (bpp == 4) { int v = b[row + x / 2]; int nib = (x % 2 == 0) ? (v >> 4) : (v & 0xF); g = GrayFromPalette(b, 40 + nib * 4); }
                else if (bpp == 1) { int v = b[row + x / 8]; int bit = (v >> (7 - x % 8)) & 1; g = GrayFromPalette(b, 40 + bit * 8); }
                else if (bpp == 24) g = (b[idx] + b[idx + 1] + b[idx + 2]) / 3;
                else g = b[idx + 2]; // BGRA 的 G 近似
                gray[y * w + x] = g < 170 ? Dark : Blank;
            }
        }

        // 缩放到统一网格（最近邻，线段检测足够）
        var resized = new byte[targetW * targetH];
        for (int y = 0; y < targetH; y++)
            for (int x = 0; x < targetW; x++)
            {
                int sx = Math.Min(w - 1, x * w / targetW), sy = Math.Min(h - 1, y * h / targetH);
                resized[y * targetW + x] = gray[sy * w + sx];
            }
        return new Bitmap { W = targetW, H = targetH, Pixels = resized };
    }

    private static int GrayFromPalette(byte[] b, int off)
    {
        if (off + 2 >= b.Length) return 255;
        return (b[off] + b[off + 1] + b[off + 2]) / 3;
    }

    // ---------- IR 栅格化 ----------
    private static Bitmap RasterizeIr(IrDocument ir, int w, int h)
    {
        var pix = new byte[w * h];
        var coords = new List<double[]>();
        foreach (var e in ir.Entities)
        {
            if (e.Points is { Count: > 0 }) coords.AddRange(e.Points);
            else if (e.Start is not null) { coords.Add(e.Start); if (e.End is not null) coords.Add(e.End); }
            else if (e.Center is not null) coords.Add(e.Center);
        }
        if (coords.Count == 0) return new Bitmap { W = w, H = h, Pixels = pix };

        double minX = coords.Min(p => p[0]), maxX = coords.Max(p => p[0]);
        double minY = coords.Min(p => p[1]), maxY = coords.Max(p => p[1]);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        // 等比缩放 + 居中，避免预览图与 IR 长宽比差异导致误判
        double scale = Math.Min((w - 4) / spanX, (h - 4) / spanY);
        int ox = (int)((w - spanX * scale) / 2), oy = (int)((h - spanY * scale) / 2);
        int X(double x) => Math.Clamp(ox + (int)((x - minX) * scale), 0, w - 1);
        int Y(double y) => Math.Clamp(h - 1 - oy - (int)((y - minY) * scale), 0, h - 1); // CAD Y 向上，图像 Y 向下

        void Line(double[] a, double[] b)
        {
            if (a is not { Length: >= 2 } || b is not { Length: >= 2 }) return;
            Bresenham(pix, w, h, X(a[0]), Y(a[1]), X(b[0]), Y(b[1]));
        }

        foreach (var e in ir.Entities)
        {
            if (e.Points is { Count: >= 2 })
                for (int i = 0; i < e.Points.Count - 1; i++) Line(e.Points[i], e.Points[i + 1]);
            else if (e.Start is not null && e.End is not null) Line(e.Start, e.End);
            else if (e.Center is not null && e.Radius > 0)
            {
                int cx = X(e.Center[0]), cy = Y(e.Center[1]), r = Math.Max(1, (int)(e.Radius * scale));
                for (int a = 0; a < 48; a++)
                {
                    double t0 = a * Math.PI / 24, t1 = (a + 1) * Math.PI / 24;
                    Bresenham(pix, w, h,
                        Math.Clamp(cx + (int)(r * Math.Cos(t0)), 0, w - 1), Math.Clamp(cy + (int)(r * Math.Sin(t0)), 0, h - 1),
                        Math.Clamp(cx + (int)(r * Math.Cos(t1)), 0, w - 1), Math.Clamp(cy + (int)(r * Math.Sin(t1)), 0, h - 1));
                }
            }
        }
        return new Bitmap { W = w, H = h, Pixels = pix };
    }

    private static void Bresenham(byte[] pix, int w, int h, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            pix[y0 * w + x0] = Dark;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ---------- 比对 ----------
    private static bool NearDark(byte[] pix, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || xx >= GridW || yy < 0 || yy >= GridH) continue;
                if (pix[yy * GridW + xx] == Dark) return true;
            }
        return false;
    }

    private static void Compare(byte[] preview, byte[] rendered, ImageVerificationReport report)
    {
        int n = preview.Length;
        int tp = 0, fp = 0, fn = 0;
        // 1 像素容差：缩略图线宽 2~3px，IR 栅格 1px，逐点比对会系统性低估一致度
        for (int y = 0; y < GridH; y++)
            for (int x = 0; x < GridW; x++)
            {
                int i = y * GridW + x;
                bool a = preview[i] == Dark, b = NearDark(rendered, x, y);
                if (a && b) tp++;
                else if (a) fn++;  // 预览有线、IR 没有 → 疑似漏解析
                else if (b) fp++;  // IR 有线、预览没有 → 疑似多解析
            }
        int union = tp + fp + fn;
        report.GlobalIoU = union == 0 ? 0 : (double)tp / union;
        report.PreviewCoverage = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        report.IrPrecision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);

        // 分区定位：12×8 网格，每格 IoU < 0.15 且预览有足够线条 → 低一致区域
        const int cols = 12, rows = 8;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x0 = c * GridW / cols, x1 = (c + 1) * GridW / cols;
                int y0 = r * GridH / rows, y1 = (r + 1) * GridH / rows;
                int t = 0, f = 0, n2 = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * GridW + x;
                        bool a = preview[i] == Dark, b = rendered[i] == Dark;
                        n2 += a ? 1 : 0;
                        if (a && b) t++;
                        else if (a) f++;
                    }
                double iou = t + f == 0 ? 1 : (double)t / (t + f);
                if (n2 >= 8 && iou < 0.15)
                    report.Regions.Add(new LowConsistencyRegion
                    {
                        Row = r, Col = c, IoU = Math.Round(iou, 3),
                        Hint = f > t ? "预览有线而解析缺失（疑似漏解析/图层被跳过）" : "解析线与预览不符（疑似误判）"
                    });
            }
    }
}
