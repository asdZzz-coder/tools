using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZZZ
{
    public record Field(string Label, string Initial = "", string Hint = "");

    /// <summary>統一風格的訊息/確認/輸入對話框。</summary>
    public partial class DialogWindow : Window
    {
        enum Kind { Info, Success, Error, Question, Danger, Input }

        readonly List<TextBox> boxes = [];
        Func<string[], string?>? validate;
        public string[]? Values { get; private set; }

        DialogWindow() => InitializeComponent();

        void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void OnOk(object sender, RoutedEventArgs e)
        {
            var vals = boxes.Select(b => b.Text.Trim()).ToArray();
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
                                       Func<string[], string?>? validate = null, string okText = "確定")
        {
            var d = Build(owner, Kind.Input, title, message);
            d.OkBtn.Content = okText;
            d.validate = validate;
            foreach (var f in fields)
            {
                d.Fields.Children.Add(new TextBlock
                {
                    Text = f.Label, Style = (Style)d.FindResource("FieldLabel"), Margin = new Thickness(2, 14, 0, 6)
                });
                var tb = new TextBox { Text = f.Initial, Tag = f.Hint };
                d.boxes.Add(tb);
                d.Fields.Children.Add(tb);
            }
            d.Loaded += (_, _) =>
            {
                if (d.boxes.Count == 0) return;
                d.boxes[0].Focus();
                d.boxes[0].SelectAll();
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
