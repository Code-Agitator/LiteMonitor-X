using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// LiteMonitor UI Utilities (Refactored)
    /// Logic delegated to MetricUtils.
    /// This class now focuses on GDI+ Rendering helpers and Resource Management.
    /// </summary>
    public static class UIUtils
    {
        // ============================================================
        // 1. String Interning (Memory Optimization)
        // ============================================================
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new object();

        public static string Intern(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            lock (_poolLock)
            {
                if (_stringPool.TryGetValue(str, out var pooled)) return pooled;
                _stringPool[str] = str;
                return str;
            }
        }

        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        // ============================================================
        // 2. DPI Scaling
        // ============================================================
        public static float ScaleFactor { get; set; } = 1.0f;

        public static int S(int px) => (int)(px * ScaleFactor);
        public static float S(float px) => px * ScaleFactor;
        public static Size S(Size size) => new Size(S(size.Width), S(size.Height));
        public static Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        // ============================================================
        // 3. GDI+ Resource Cache (Brushes & Fonts)
        // ============================================================
        private static readonly Dictionary<string, SolidBrush> _brushCache = new(16);
        private static readonly Dictionary<string, Pen> _penCache = new(16);
        private static readonly Dictionary<string, Font> _fontCache = new(16);
        private static readonly object _brushLock = new object();
        private const int MAX_BRUSH_CACHE = 32;

        public static Pen GetPen(Color color, float width = 1.0f)
        {
            string key = $"{color.ToArgb()}_{width:F1}";
            lock (_brushLock)
            {
                if (!_penCache.TryGetValue(key, out var pen))
                {
                    if (_penCache.Count >= MAX_BRUSH_CACHE)
                    {
                        foreach (var p in _penCache.Values) p.Dispose();
                        _penCache.Clear();
                    }
                    pen = new Pen(color, width);
                    _penCache[key] = pen;
                }
                return pen;
            }
        }

        public static SolidBrush GetBrush(string color)
        {
            if (string.IsNullOrEmpty(color)) return (SolidBrush)Brushes.Transparent;

            lock (_brushLock)
            {
                if (!_brushCache.TryGetValue(color, out var br))
                {
                    // Cache eviction policy
                    if (_brushCache.Count >= MAX_BRUSH_CACHE)
                    {
                        var keysToRemove = _brushCache.Keys.Take(_brushCache.Count / 2).ToList();
                        foreach (var k in keysToRemove)
                        {
                            if (_brushCache.TryGetValue(k, out var oldBrush))
                            {
                                oldBrush.Dispose();
                                _brushCache.Remove(k);
                            }
                        }
                    }

                    br = new SolidBrush(ThemeManager.ParseColor(color));
                    _brushCache[color] = br;
                }
                return br;
            }
        }

        public static Font GetFont(string familyName, float size, bool bold)
        {
            string key = $"{familyName}_{size}_{bold}";
            lock (_brushLock)
            {
                if (!_fontCache.TryGetValue(key, out var font))
                {
                    try 
                    {
                        var style = bold ? FontStyle.Bold : FontStyle.Regular;
                        font = new Font(familyName, size, style);
                    }
                    catch
                    {
                        font = new Font(SystemFonts.DefaultFont.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                    }
                    _fontCache[key] = font;
                }
                return font;
            }
        }

        public static void ClearBrushCache()
        {
            lock (_brushLock)
            {
                foreach (var b in _brushCache.Values) b.Dispose();
                _brushCache.Clear();

                foreach (var p in _penCache.Values) p.Dispose();
                _penCache.Clear();
                
                foreach (var f in _fontCache.Values) f.Dispose();
                _fontCache.Clear();
            }
        }

        // ============================================================
        // 4. Theme Helpers
        // ============================================================

        public static Color GetStateColor(int state, Theme t, bool isValueText = true)
        {
            if (state == MetricUtils.STATE_CRIT) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (state == MetricUtils.STATE_WARN) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }

        // ============================================================
        // 5. Drawing Helpers
        // ============================================================

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;

            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }

        public static void DrawBar(Graphics g, MetricItem item, Theme t)
        {
            if (item.BarRect.Width <= 0 || item.BarRect.Height <= 0) return;

            // Background
            using (var bgPath = RoundRect(item.BarRect, item.BarRect.Height / 2))
            {
                g.FillPath(GetBrush(t.Color.BarBackground), bgPath);
            }

            // 2. Value Bar
            // [Refactor] CachedPercent already includes visual correction (min 5%, max 100%) via MetricUtils.GetProgressValue
            double percent = item.CachedPercent; 
            
            int w = (int)(item.BarRect.Width * percent);
            if (w < 1 && percent > 0) w = 1; // 绝对防御：只要有百分比，至少画1像素

            // Color
            Color barColor = GetStateColor(item.CachedColorState, t, false);

            if (w > 0)
            {
                Rectangle bar = new Rectangle(item.BarRect.X, item.BarRect.Y, w, item.BarRect.Height);
                if (bar.Width > 0 && bar.Height > 0)
                {
                    using (var fgPath = RoundRect(bar, bar.Height / 2))
                    using (var brush = new SolidBrush(barColor))
                    {
                        g.FillPath(brush, fgPath);
                    }
                }
            }
        }

        // ============================================================
        // 6. Windows 11 风格卡片（圆角 + 极淡描边 + 顶部高光线）
        // ============================================================
        public static GraphicsPath RoundRectF(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;

            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// 只绘制 Fluent 描边：1px 极淡轮廓 + 顶部高光线（Windows 11 卡片的签名细节）。
        /// 用半像素偏移让 1px 线条落在一个像素内，避免抗锯齿把细线糊成 2px。
        /// </summary>
        public static void DrawCardOutline(Graphics g, Rectangle r, int radius, Color stroke, Color highlight)
        {
            if (r.Width <= 0 || r.Height <= 0) return;

            if (stroke.A > 0)
            {
                var inner = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                using var path = RoundRectF(inner, radius);
                g.DrawPath(GetPen(stroke, 1f), path);
            }

            if (highlight.A > 0 && r.Width > radius * 2 + 2)
            {
                int x1 = r.X + radius;
                int x2 = r.Right - radius - 1;
                if (x2 > x1)
                    g.DrawLine(GetPen(highlight, 1f), x1, r.Y + 1f, x2, r.Y + 1f);
            }
        }

        /// <summary>Windows 11 风格卡片：填充 + 描边 + 顶部高光线。</summary>
        public static void DrawCard(Graphics g, Rectangle r, int radius, Color fill, Color stroke, Color highlight)
        {
            if (r.Width <= 0 || r.Height <= 0) return;

            using (var brush = new SolidBrush(fill))
            using (var path = RoundRect(r, radius))
            {
                g.FillPath(brush, path);
            }

            DrawCardOutline(g, r, radius, stroke, highlight);
        }

        // ============================================================
        // 7. 4px 间距栅格
        // ============================================================
        /// <summary>把间距吸附到 4px 栅格（Windows 11 的布局节拍）。</summary>
        public static int Grid4(int px) => (int)Math.Round(px / 4.0) * 4;
        public static int Grid4(float px) => (int)Math.Round(px / 4.0) * 4;

        // ============================================================
        // 8. 等宽数字（模拟 OpenType 的 tnum 特性）
        //     GDI/GDI+ 无法开启 OpenType 特性，这里用固定步进模拟，
        //     保证数值刷新时宽度不跳动。
        // ============================================================
        private static readonly Dictionary<string, int> _digitAdvanceCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _textWidthCache = new(StringComparer.Ordinal);
        private static readonly object _measureLock = new object();

        private static string FontKey(Font font)
            => $"{font.Name}|{font.SizeInPoints:F2}|{(int)font.Style}";

        private static int MeasureText(string s, Font font)
        {
            if (string.IsNullOrEmpty(s)) return 0;

            string key = $"{FontKey(font)}|{s}";
            lock (_measureLock)
            {
                if (_textWidthCache.TryGetValue(key, out int cached)) return cached;
            }

            int width = TextRenderer.MeasureText(s, font, new Size(int.MaxValue, int.MaxValue),
                                                TextFormatFlags.NoPadding).Width;

            // 纯空白等测量为 0 的兜底，避免单词粘连
            if (width <= 0)
                width = Math.Max(1, font.Height / 4);

            lock (_measureLock)
            {
                if (_textWidthCache.Count > 512) _textWidthCache.Clear();
                _textWidthCache[key] = width;
            }
            return width;
        }

        /// <summary>数字的最大字宽：所有数字按该宽度步进。</summary>
        private static int DigitAdvance(Font font)
        {
            string key = FontKey(font);
            lock (_measureLock)
            {
                if (_digitAdvanceCache.TryGetValue(key, out int cached)) return cached;
            }

            int max = 0;
            for (char c = '0'; c <= '9'; c++)
            {
                int w = MeasureText(c.ToString(), font);
                if (w > max) max = w;
            }

            lock (_measureLock) { _digitAdvanceCache[key] = max; }
            return max;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static void DrawRun(Graphics g, string s, Font font, int x, Rectangle rect, Color color, TextFormatFlags vertical)
        {
            if (string.IsNullOrEmpty(s)) return;

            var target = new Rectangle(x, rect.Y, Math.Max(1, rect.Width - (x - rect.X)), rect.Height);
            TextRenderer.DrawText(g, s, font, target, color,
                TextFormatFlags.Left | vertical |
                TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
        }

        /// <summary>右对齐的等宽数字绘制（数值列）。</summary>
        public static void DrawTabularRight(Graphics g, string text, Font font, Rectangle rect, Color color)
            => DrawTabular(g, text, font, rect, color, center: false, bottom: false);

        /// <summary>居中的等宽数字绘制（双列项：读取/写入、上传/下载）。</summary>
        public static void DrawTabularCentered(Graphics g, string text, Font font, Rectangle rect, Color color, bool bottomAlign = false)
            => DrawTabular(g, text, font, rect, color, center: true, bottom: bottomAlign);

        /// <summary>
        /// 绘制文本，数字使用固定步进（等宽数字）。
        /// GDI/GDI+ 无法开启 OpenType 的 tnum 特性，这里用固定步进模拟，
        /// 保证数值刷新时宽度不跳动。非数字的连续片段整体绘制，保留正常字距与字形组合。
        /// </summary>
        private static void DrawTabular(Graphics g, string text, Font font, Rectangle rect, Color color, bool center, bool bottom)
        {
            if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;

            var vertical = bottom ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter;

            int digitAdv = DigitAdvance(font);
            if (digitAdv <= 0)
            {
                // 测量异常时退回普通绘制
                TextRenderer.DrawText(g, text, font, rect, color,
                    (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right) |
                    vertical | TextFormatFlags.NoPadding);
                return;
            }

            // 1. 计算总宽度
            int total = 0;
            for (int i = 0; i < text.Length;)
            {
                if (IsDigit(text[i])) { total += digitAdv; i++; continue; }

                int j = i;
                while (j < text.Length && !IsDigit(text[j])) j++;
                total += MeasureText(text.Substring(i, j - i), font);
                i = j;
            }

            // 2. 定位起点，逐段绘制
            int x = center ? rect.X + (rect.Width - total) / 2 : rect.Right - total;
            for (int i = 0; i < text.Length;)
            {
                if (IsDigit(text[i]))
                {
                    DrawRun(g, text[i].ToString(), font, x, rect, color, vertical);
                    x += digitAdv;
                    i++;
                    continue;
                }

                int j = i;
                while (j < text.Length && !IsDigit(text[j])) j++;
                string run = text.Substring(i, j - i);
                DrawRun(g, run, font, x, rect, color, vertical);
                x += MeasureText(run, font);
                i = j;
            }
        }
    }
}
