using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZZZ
{
    public partial class CategoryWindow : Window
    {
        readonly Database db;
        public bool Changed { get; private set; }

        public CategoryWindow(Database db)
        {
            InitializeComponent();
            this.db = db;
            Reload();
        }

        void Reload()
        {
            ExpList.ItemsSource = db.Categories(Database.Expense);
            IncList.ItemsSource = db.Categories(Database.Income);
        }

        void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();

        void OnBoxKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Add(sender == ExpBox ? Database.Expense : Database.Income);
            e.Handled = true;
        }

        void OnAdd(object sender, RoutedEventArgs e) => Add((string)((Button)sender).Tag);

        void Add(string type)
        {
            var box = type == Database.Expense ? ExpBox : IncBox;
            var name = box.Text.Trim();
            if (name == "") { box.Focus(); return; }
            if (!db.AddCategory(type, name))
            {
                DialogWindow.Info(this, "提示", "這個分類已經存在");
                return;
            }
            box.Clear();
            box.Focus();
            Changed = true;
            Reload();
        }

        void OnDelete(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var name = (string)btn.Tag;
            DependencyObject? p = btn;
            while (p != null && p is not ListBox) p = VisualTreeHelper.GetParent(p);
            if (p is not ListBox list) return;
            var type = (string)list.Tag;
            if (!DialogWindow.Confirm(this, "刪除分類", $"刪除{type}分類「{name}」?\n已存在的記錄不受影響。", "刪除", danger: true))
                return;
            db.DeleteCategory(type, name);
            Changed = true;
            Reload();
        }
    }
}
