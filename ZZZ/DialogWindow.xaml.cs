using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ZZZ
{
    /// <param name="IsDate">日期欄:可點日曆選,也可直接輸入 YYYY-MM-DD;值為輸入框裡的文字</param>
    /// <param name="Options">有值時改用下拉選單,值為選到的那一項(Initial 為預設選項)</param>
    public record Field(string Label, string Initial = "", string Hint = "", bool IsDate = false, string[]? Options = null);

    /// <summary>統一風格的訊息/確認/輸入對話框。</summary>
    public partial class DialogWindow : Window
    {
        enum Kind { Info, Success, Error, Question, Danger, Input }

        readonly List<Func<string>> values = []; // 每個欄位目前的值
        Control? firstInput;
        Func<string[], string?>? validate;
        public string[]? Values { get; private set; }

        DialogWindow() => InitializeComponent();

        void OnDrag(object sender, MouseButtonEventArgs e)
        {
            // 日曆彈出視窗裡的點擊也會傳到這裡,只有點在對話框本身才拖曳
            if (e.OriginalSource is Visual v && PresentationSource.FromVisual(v) != PresentationSource.FromVisual(this)) return;
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        /// <summary>
        /// 日期欄:一般輸入框加上日曆按鈕。不用 DatePicker,因為它會把打錯的日期默默清掉,
        /// 這裡打什麼就保留什麼,交給 validate 檢查;在日曆點選則填入 yyyy-MM-dd。
        /// </summary>
        FrameworkElement DateBox(TextBox tb)
        {
            tb.Padding = new Thickness(10, 0, 38, 0); // 讓出日曆按鈕的位置
            var cal = new System.Windows.Controls.Calendar
            {
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                LayoutTransform = new ScaleTransform(1.25, 1.25), // 預設日曆太小,放大好點
            };
            var popup = new Popup
            {
                PlacementTarget = tb, Placement = PlacementMode.Bottom, VerticalOffset = 2,
                StaysOpen = false, AllowsTransparency = true,
                Child = new Border
                {
                    Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(6),
                    BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 12, 12), Child = cal,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.18 },
                },
            };
            var btn = new Button
            {
                Style = (Style)FindResource("IconButton"), Content = "", Width = 32, Height = 30, FontSize = 13,
                Margin = new Thickness(0, 0, 3, 0), HorizontalAlignment = HorizontalAlignment.Right,
                Focusable = false, ToolTip = "選擇日期",
            };
            btn.Click += (_, _) =>
            {
                if (popup.IsOpen) { popup.IsOpen = false; return; }
                var day = DateTime.TryParse(tb.Text, out var d) ? d.Date : DateTime.Today;
                cal.SelectedDate = DateTime.TryParse(tb.Text, out _) ? day : null;
                cal.DisplayDate = day;
                popup.IsOpen = true;
            };
            void Apply()
            {
                if (cal.SelectedDate is not DateTime picked) return;
                tb.Text = picked.ToString("yyyy-MM-dd");
                popup.IsOpen = false;
                tb.Focus();
                tb.CaretIndex = tb.Text.Length;
            }
            // 點日期才填入(點同一天也算);切換月份或用方向鍵移動不會關閉
            cal.PreviewMouseUp += (_, e) =>
            {
                if (Mouse.Captured is CalendarItem) Mouse.Capture(null); // Calendar 會抓住滑鼠,不放開的話要多點一下才有反應
                for (var o = e.OriginalSource as DependencyObject; o != null && o != cal; o = VisualTreeHelper.GetParent(o))
                    if (o is CalendarDayButton) { Apply(); return; }
            };
            cal.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
                else if (e.Key == Key.Escape) { popup.IsOpen = false; tb.Focus(); e.Handled = true; }
            };
            var grid = new Grid();
            grid.Children.Add(tb);
            grid.Children.Add(btn);
            grid.Children.Add(popup);
            return grid;
        }

        void OnOk(object sender, RoutedEventArgs e)
        {
            var vals = values.Select(v => v()).ToArray();
            var err = validate?.Invoke(vals);
            if (err != null)
            {
                ErrorText.Text = err;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            Values = vals;
            DialogResult = true;
        }

        static DialogWindow Build(Window? owner, Kind kind, string title, string message)
        {
            var d = new DialogWindow { Title = title };
            owner ??= Application.Current.MainWindow;
            if (owner != null && owner.IsLoaded) d.Owner = owner;
            else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            d.TitleText.Text = title;
            d.MessageText.Text = message;
            if (message == "") d.MessageText.Visibility = Visibility.Collapsed;
            (string icon, string fg, string bg) = kind switch
            {
                Kind.Success => ("", "IncBrush", "IncSoftBrush"),
                Kind.Error => ("", "ExpBrush", "ExpSoftBrush"),
                Kind.Danger => ("", "ExpBrush", "ExpSoftBrush"),
                Kind.Question => ("", "InkBrush", "GhostBrush"),
                Kind.Input => ("", "InkBrush", "GhostBrush"),
                _ => ("", "InkBrush", "GhostBrush"),
            };
            d.IconText.Text = icon;
            d.IconText.Foreground = (Brush)d.FindResource(fg);
            d.IconBg.Background = (Brush)d.FindResource(bg);
            return d;
        }

        static bool Run(DialogWindow d)
        {
            var main = d.Owner as MainWindow;
            main?.SetDim(true);
            try { return d.ShowDialog() == true; }
            finally { main?.SetDim(false); }
        }

        public static void Info(Window? owner, string title, string message) => Alert(owner, Kind.Info, title, message);
        public static void Success(Window? owner, string title, string message) => Alert(owner, Kind.Success, title, message);
        public static void Error(Window? owner, string title, string message) => Alert(owner, Kind.Error, title, message);

        static void Alert(Window? owner, Kind kind, string title, string message)
        {
            var d = Build(owner, kind, title, message);
            d.CancelBtn.Visibility = Visibility.Collapsed;
            d.OkBtn.Content = "知道了";
            Run(d);
        }

        public static bool Confirm(Window? owner, string title, string message,
                                   string okText = "確定", bool danger = false)
        {
            var d = Build(owner, danger ? Kind.Danger : Kind.Question, title, message);
            d.OkBtn.Content = okText;
            if (danger) d.OkBtn.Style = (Style)d.FindResource("DangerSolidButton");
            return Run(d);
        }

        /// <param name="validate">回傳錯誤訊息則留在對話框;回傳 null 表示通過。</param>
        public static string[]? Prompt(Window? owner, string title, string message, Field[] fields,
                                       Func<string[], string?>? validate = null, string okText = "確定", bool danger = false)
        {
            var d = Build(owner, danger ? Kind.Danger : Kind.Input, title, message);
            d.OkBtn.Content = okText;
            if (danger) d.OkBtn.Style = (Style)d.FindResource("DangerSolidButton");
            d.validate = validate;
            foreach (var f in fields)
            {
                d.Fields.Children.Add(new TextBlock
                {
                    Text = f.Label, Style = (Style)d.FindResource("FieldLabel"), Margin = new Thickness(2, 14, 0, 6)
                });
                if (f.Options != null)
                {
                    var cb = new ComboBox
                    {
                        ItemsSource = f.Options,
                        SelectedItem = f.Options.Contains(f.Initial) ? f.Initial : f.Options.FirstOrDefault(),
                    };
                    d.values.Add(() => cb.SelectedItem as string ?? "");
                    d.firstInput ??= cb;
                    d.Fields.Children.Add(cb);
                    continue;
                }
                var tb = new TextBox { Text = f.Initial, Tag = f.Hint };
                d.values.Add(() => tb.Text.Trim());
                d.firstInput ??= tb;
                d.Fields.Children.Add(f.IsDate ? d.DateBox(tb) : tb);
            }
            d.Loaded += (_, _) =>
            {
                d.firstInput?.Focus();
                (d.firstInput as TextBox)?.SelectAll();
            };
            return Run(d) ? d.Values : null;
        }

        /// <summary>顯示數個大型選項按鈕,回傳所選索引;取消回傳 null。</summary>
        public static int? Choose(Window? owner, string title, string message,
                                  (string Icon, string Title, string Description)[] options)
        {
            var d = Build(owner, Kind.Question, title, message);
            d.OkBtn.Visibility = Visibility.Collapsed;
            int? chosen = null;
            for (int i = 0; i < options.Length; i++)
            {
                var (icon, head, desc) = options[i];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                var ico = new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(9),
                    Background = (Brush)d.FindResource("GhostBrush"),
                    Child = new TextBlock { Text = icon, Style = (Style)d.FindResource("Icon"), FontSize = 15 },
                };
                var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                text.Children.Add(new TextBlock
                {
                    Text = desc, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)d.FindResource("MutedBrush"),
                });
                Grid.SetColumn(text, 1);
                grid.Children.Add(ico);
                grid.Children.Add(text);
                var btn = new Button
                {
                    Style = (Style)d.FindResource("OutlineButton"), Content = grid, Height = double.NaN,
                    Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 12, 0, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = i,
                };
                btn.Click += (s, _) => { chosen = (int)((Button)s).Tag; d.DialogResult = true; };
                d.Fields.Children.Add(btn);
            }
            return Run(d) ? chosen : null;
        }

        public static bool TryParseAmount(string s, out double value) =>
            double.TryParse(s.Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
