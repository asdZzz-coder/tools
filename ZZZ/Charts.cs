using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ZZZ
{
    /// <summary>一個支出分類的統計:圓環圖的一塊,也是右側列表的一列。</summary>
    public record CategoryStat(string Name, double Amount, int Count, double Share, Brush Brush)
    {
        public string AmountText => Amount.ToString("N0");
        public string ShareText => $"{Share:P1}";
        public string Detail => $"{Count} 筆";
    }

    /// <summary>
    /// 圖表專用的低飽和色。介面其他地方的顏色只用來表示意義(綠收入、紅支出、橘逾期),
    /// 分類需要分得出彼此才在這裡另外配色,並避開綠、紅、橘。
    /// </summary>
    public static class ChartPalette
    {
        static readonly Brush[] Brushes = new[]
        {
            "#4E79A7", "#D4A373", "#8E7DBE", "#5E9EA0", "#C38D9E",
            "#B8A04A", "#7D8CA3", "#A0785A", "#6C8EBF", "#B39CD0",
        }.Select(Make).ToArray();

        static readonly Brush Other = Make("#B8BEC7");

        static Brush Make(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>依分類在「管理分類」中的順序配色,換月份顏色也不會變;「其他」固定灰色。</summary>
        public static Brush For(string category, IReadOnlyList<string> categories)
        {
            if (category == "其他") return Other;
            int i = categories.ToList().IndexOf(category);
            if (i < 0) i = categories.Count + category.Sum(c => c); // 已刪除的分類:用名稱算出固定的顏色
            return Brushes[i % Brushes.Length];
        }
    }

    /// <summary>支出分類圓環圖。中間顯示總額,滑過某一塊改顯示該分類。</summary>
    public class DonutChart : Grid
    {
        readonly Canvas canvas = new();
        readonly TextBlock title = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBlock value = new() { FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBlock sub = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        readonly Dictionary<string, Path> paths = [];
        IReadOnlyList<CategoryStat> slices = [];
        (string Title, string Value, string Sub) total = ("", "", "");

        public event Action<CategoryStat>? SliceClicked;
        /// <summary>滑鼠移到某一塊(null 為移開),讓右側列表跟著標示。</summary>
        public event Action<string?>? HoverChanged;

        public DonutChart()
        {
            Children.Add(canvas);
            var center = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, MaxWidth = 130,
            };
            foreach (var t in new[] { title, value, sub })
            {
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                center.Children.Add(t);
            }
            Children.Add(center);
            Loaded += (_, _) =>
            {
                title.Foreground = sub.Foreground = (Brush)FindResource("MutedBrush");
                value.Foreground = (Brush)FindResource("TextBrush");
            };
            SizeChanged += (_, _) => Draw();
        }

        public void SetData(IReadOnlyList<CategoryStat> data, string totalTitle, string totalValue, string totalSub)
        {
            slices = data;
            total = (totalTitle, totalValue, totalSub);
            Draw();
            Highlight(null);
        }

        void Draw()
        {
            canvas.Children.Clear();
            paths.Clear();
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size < 40) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            const double Grow = 1.06; // 滑過時放大的倍率,外圈要預留空間
            double outer = size / 2 / Grow - 1, inner = outer * 0.64;
            double sum = slices.Sum(s => s.Amount);
            if (sum <= 0)
            {
                canvas.Children.Add(Ring(c, outer, inner, 0, 360, (Brush)FindResource("GhostBrush")));
                return;
            }
            double start = -90; // 從正上方順時針畫
            foreach (var s in slices)
            {
                double sweep = s.Amount / sum * 360;
                var path = Ring(c, outer, inner, start, sweep, s.Brush);
                path.Stroke = Brushes.White; // 白色細縫隔開每一塊
                path.StrokeThickness = slices.Count > 1 ? 2 : 0;
                path.Cursor = Cursors.Hand;
                path.RenderTransform = new ScaleTransform(1, 1, c.X, c.Y);
                path.MouseEnter += (_, _) => { Highlight(s.Name); HoverChanged?.Invoke(s.Name); };
                path.MouseLeave += (_, _) => { Highlight(null); HoverChanged?.Invoke(null); };
                path.MouseLeftButtonUp += (_, _) => SliceClicked?.Invoke(s);
                canvas.Children.Add(path);
                paths[s.Name] = path;
                start += sweep;
            }
        }

        /// <summary>放大指定分類、淡化其他分類,中間改顯示該分類;null 還原成總額。</summary>
        public void Highlight(string? name)
        {
            foreach (var (n, p) in paths)
            {
                var t = (ScaleTransform)p.RenderTransform;
                t.ScaleX = t.ScaleY = n == name ? 1.06 : 1;
                p.Opacity = name == null || n == name ? 1 : 0.4;
            }
            var s = name == null ? null : slices.FirstOrDefault(x => x.Name == name);
            (title.Text, value.Text, sub.Text) = s == null ? total : (s.Name, s.AmountText, s.ShareText);
        }

        static Path Ring(Point c, double outer, double inner, double startDeg, double sweepDeg, Brush fill)
        {
            Geometry g;
            if (sweepDeg >= 359.99) // 只有一塊時 ArcSegment 畫不出整圈,改用兩個圓相減
                g = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(c, outer, outer), new EllipseGeometry(c, inner, inner));
            else
            {
                double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
                Point At(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                bool large = sweepDeg > 180;
                var fig = new PathFigure { StartPoint = At(outer, a0), IsClosed = true };
                fig.Segments.Add(new ArcSegment(At(outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
                fig.Segments.Add(new LineSegment(At(inner, a1), true));
                fig.Segments.Add(new ArcSegment(At(inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
                g = new PathGeometry([fig]);
            }
            return new Path { Data = g, Fill = fill };
        }
    }

    /// <summary>每月收支長條圖:綠色收入、紅色支出,折線為結餘。每個月是一個可點的欄位。</summary>
    public class MonthBarChart : Canvas
    {
        IReadOnlyList<MonthTotal> data = [];
        string? selected;

        public event Action<string>? MonthClicked;

        public MonthBarChart()
        {
            ClipToBounds = true;
            SizeChanged += (_, _) => Draw();
        }

        /// <param name="selectedMonth">要標示的月份(yyyy-MM),null 表示不標示</param>
        public void SetData(IReadOnlyList<MonthTotal> months, string? selectedMonth)
        {
            data = months;
            selected = selectedMonth;
            Draw();
        }

        void Draw()
        {
            Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 80 || h < 80 || data.Count == 0) return;
            Brush B(string key) => (Brush)FindResource(key);

            const double Left = 46, Top = 8, Bottom = 36; // 左側刻度、下方月份文字的空間
            double max = data.Max(m => Math.Max(Math.Max(m.Income, m.Expense), m.Net));
            double min = Math.Min(0, data.Min(m => m.Net));
            if (max <= 0 && min >= 0) max = 1000;
            double step = NiceStep((max - min) / 4);
            double hi = Math.Ceiling(max / step) * step, lo = Math.Floor(min / step) * step;
            double plotH = h - Top - Bottom, plotW = w - Left;
            double Y(double v) => Top + (hi - v) / (hi - lo) * plotH;

            // 格線與刻度
            for (int k = 0; k <= Math.Round((hi - lo) / step); k++)
            {
                double v = lo + k * step, y = Math.Round(Y(v)) + 0.5;
                Children.Add(new Line
                {
                    X1 = Left, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1,
                    Stroke = B(Math.Abs(v) < step / 2 ? "LineStrongBrush" : "LineBrush"),
                });
                var label = new TextBlock
                {
                    Text = Axis(v), FontSize = 11, Foreground = B("SubtleBrush"),
                    Width = Left - 8, TextAlignment = TextAlignment.Right,
                };
                SetLeft(label, 0);
                SetTop(label, y - 8);
                Children.Add(label);
            }

            double colW = plotW / data.Count, barW = Math.Clamp(colW * 0.26, 4, 16), zero = Y(0);
            var line = new Polyline { Stroke = B("InkBrush"), StrokeThickness = 1.6, IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round };
            var dots = new List<UIElement>();
            for (int i = 0; i < data.Count; i++)
            {
                var m = data[i];
                double x0 = Left + i * colW, cx = x0 + colW / 2;
                bool isSel = m.Month == selected;
                var month = DateTime.ParseExact(m.Month, "yyyy-MM", null);

                // 整欄是一個按鈕(放在最底層),長條與文字都不攔截滑鼠
                var col = new Button
                {
                    Style = (Style)FindResource("ChartColumn"), Width = colW, Height = h, Tag = m.Month,
                    Background = isSel ? B("SelBrush") : Brushes.Transparent,
                    ToolTip = $"{month:yyyy 年 M 月}\n收入 {m.Income:N0}\n支出 {m.Expense:N0}\n結餘 {(m.Net > 0 ? "+" : "")}{m.Net:N0}",
                };
                AutomationProperties.SetName(col, m.Month);
                col.Click += (s, _) => MonthClicked?.Invoke((string)((Button)s).Tag);
                SetLeft(col, x0);
                SetTop(col, 0);
                Children.Add(col);

                AddBar(cx - barW - 1, m.Income, B("IncBrush"));
                AddBar(cx + 1, m.Expense, B("ExpBrush"));

                var text = new TextBlock
                {
                    Text = i == 0 || month.Month == 1 ? $"{month.Month}月\n{month.Year}" : $"{month.Month}月",
                    FontSize = 11, Width = colW, TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
                    Foreground = B(isSel ? "TextBrush" : "SubtleBrush"),
                    FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal,
                };
                SetLeft(text, x0);
                SetTop(text, h - Bottom + 6);
                Children.Add(text);

                var p = new Point(cx, Y(m.Net));
                line.Points.Add(p);
                var dot = new Ellipse { Width = 7, Height = 7, Fill = B("InkBrush"), Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
                SetLeft(dot, p.X - 3.5);
                SetTop(dot, p.Y - 3.5);
                dots.Add(dot);
            }
            Children.Add(line);
            foreach (var d in dots) Children.Add(d);

            void AddBar(double x, double v, Brush fill)
            {
                if (v <= 0) return;
                double height = Math.Max(zero - Y(v), 2);
                var r = new Rectangle
                {
                    Width = barW, Height = height, Fill = fill, IsHitTestVisible = false,
                    RadiusX = Math.Min(3, barW / 2), RadiusY = Math.Min(3, barW / 2),
                };
                SetLeft(r, x);
                SetTop(r, zero - height);
                Children.Add(r);
            }
        }

        /// <summary>把刻度間距取成 1、2、5 × 10 的次方。</summary>
        static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw))), n = raw / mag;
            return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
        }

        static string Axis(double v) => Math.Abs(v) >= 10000 ? $"{v / 10000:0.#}萬" : v.ToString("N0");
    }
}
