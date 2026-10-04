using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ZZZ
{
    public record WalletItem(long? Id, string Name, double Balance)
    {
        public bool IsAll => Id == null;
        public string Icon => Id == null ? "" : "";
        public string BalanceText => Balance.ToString("N0");
        public bool Negative => Balance < 0;
    }

    public partial class MainWindow : Window
    {
        readonly Database db = new();
        long? selWallet;          // null = 全部錢包
        bool loadingWallets;

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = $"v{Updater.CurrentVersion}";
            Title = $"簡易記帳 v{Updater.CurrentVersion}";
            TodayText.Text = DateTime.Today.ToString("yyyy 年 M 月 d 日 dddd");
            RecDate.SelectedDate = DebtDate.SelectedDate = DateTime.Today;
            MonthBox.Text = DateTime.Today.ToString("yyyy-MM");
            LoadCategories();
            RefreshAll();
            RefreshDebts();
            RefreshGoals();
            Donut.SliceClicked += s => ShowCategoryRecords(s.Name);
            Donut.HoverChanged += HighlightLegend;
            Bars.MonthClicked += m => { statsPeriod = m; RefreshStats(); };

            SourceInitialized += (_, _) => TintTitleBar();
            Loaded += (_, _) =>
            {
                Updater.CleanupTemp(); // 清掉更新後遺留的下載檔
                Updater.RetargetShortcuts(); // 工作列/桌面上指向舊版的捷徑改指新版
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += async (_, _) => { t.Stop(); await CheckUpdate(silent: true); };
                t.Start();
            };
            Closed += (_, _) => db.Dispose();
        }

        public void SetDim(bool on) => Dim.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // 讓 Windows 11 標題列與側欄同色
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void TintTitleBar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int caption = 0x00FFFFFF, border = 0x00EBE7E5; // COLORREF = 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (StatsView == null) return;
            static Visibility Show(RadioButton tab) => tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            RecordsView.Visibility = Show(TabRecords);
            DebtsView.Visibility = Show(TabDebts);
            GoalsView.Visibility = Show(TabGoals);
            StatsView.Visibility = Show(TabStats);
            RefreshStats();
        }

        /// <summary>重新載入所有分頁(匯入資料後使用)。</summary>
        void ReloadEverything()
        {
            selWallet = null;
            catFilter = null;
            LoadCategories();
            RefreshAll();
            RefreshDebts();
            RefreshGoals();
        }

        static string Money(double v) => v.ToString("N0");

        // ================= 錢包 / 側欄 =================
        void RefreshAll()
        {
            RefreshSidebar();
            var wallets = db.Wallets();
            var current = (RecWallet.SelectedItem as Wallet)?.Id;
            RecWallet.ItemsSource = wallets;
            RecWallet.SelectedItem = wallets.FirstOrDefault(w => w.Id == (selWallet ?? current)) ?? wallets.FirstOrDefault();
            RefreshRecords();
        }

        void RefreshSidebar()
        {
            var wallets = db.Wallets();
            if (selWallet != null && wallets.All(w => w.Id != selWallet)) selWallet = null;
            var items = new ObservableCollection<WalletItem> { new(null, "全部錢包", wallets.Sum(w => w.Balance)) };
            foreach (var w in wallets) items.Add(new WalletItem(w.Id, w.Name, w.Balance));
            loadingWallets = true;
            WalletList.ItemsSource = items;
            WalletList.SelectedItem = items.First(i => i.Id == selWallet);
            loadingWallets = false;
            RenameWalletBtn.IsEnabled = DeleteWalletBtn.IsEnabled = selWallet != null;
        }

        void WalletList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingWallets || WalletList.SelectedItem is not WalletItem item) return;
            selWallet = item.Id;
            RenameWalletBtn.IsEnabled = DeleteWalletBtn.IsEnabled = selWallet != null;
            if (selWallet != null && RecWallet.ItemsSource is List<Wallet> ws)
                RecWallet.SelectedItem = ws.FirstOrDefault(w => w.Id == selWallet);
            RefreshRecords();
        }

        // ---------- 拖曳錢包調整順序(「全部錢包」固定在最上面) ----------
        Point dragStart;
        WalletItem? dragItem;

        void WalletList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            dragStart = e.GetPosition(WalletList);
            dragItem = (ItemsControl.ContainerFromElement(WalletList, (DependencyObject)e.OriginalSource) as ListBoxItem)
                ?.DataContext as WalletItem;
            if (dragItem?.IsAll == true) dragItem = null;
        }

        void WalletList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(WalletList) - dragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = dragItem;
            dragItem = null;
            var items = (ObservableCollection<WalletItem>)WalletList.ItemsSource;
            var before = items.Select(i => i.Id).ToList();
            // 拖曳中 DragOver 會直接移動清單項目當作預覽,放開後再存檔;拖到清單外放開則還原
            var result = DragDrop.DoDragDrop(WalletList, item, DragDropEffects.Move);
            var after = items.Select(i => i.Id).ToList();
            if (result != DragDropEffects.Move) { RefreshSidebar(); return; }
            if (after.SequenceEqual(before)) return;
            db.ReorderWallets(after.OfType<long>().ToList());
            RefreshAll(); // 新增記錄的錢包下拉選單也跟著換順序
        }

        void WalletList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            if (e.Data.GetData(typeof(WalletItem)) is not WalletItem item) return;
            e.Effects = DragDropEffects.Move;
            var items = (ObservableCollection<WalletItem>)WalletList.ItemsSource;
            var target = (ItemsControl.ContainerFromElement(WalletList, (DependencyObject)e.OriginalSource) as ListBoxItem)
                ?.DataContext as WalletItem;
            if (target == null || target == item) return;
            int from = items.IndexOf(item), to = Math.Max(items.IndexOf(target), 1); // 不能排到「全部錢包」上面
            if (from != to) items.Move(from, to);
        }

        void WalletList_Drop(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(WalletItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        void AddWallet_Click(object sender, RoutedEventArgs e)
        {
            var v = DialogWindow.Prompt(this, "新增錢包", "建立一個新的錢包或銀行戶頭。",
                [new Field("名稱", "", "例如:現金、郵局、信用卡"), new Field("目前餘額", "0", "可為 0 或負數")],
                vals =>
                {
                    if (vals[0] == "") return "請輸入錢包名稱";
                    if (!DialogWindow.TryParseAmount(vals[1] == "" ? "0" : vals[1], out _)) return "餘額需為數字";
                    return null;
                }, "建立");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[1] == "" ? "0" : v[1], out var init);
            var id = db.AddWallet(v[0], init);
            if (id == null) { DialogWindow.Info(this, "提示", "已有相同名稱的錢包"); return; }
            selWallet = id;
            RefreshAll();
        }

        void RenameWallet_Click(object sender, RoutedEventArgs e)
        {
            if (WalletList.SelectedItem is not WalletItem { Id: long id } item) return;
            var v = DialogWindow.Prompt(this, "重新命名", "", [new Field("新的名稱", item.Name)],
                vals => vals[0] == "" ? "名稱不可空白" : null);
            if (v == null || v[0] == item.Name) return;
            if (!db.RenameWallet(id, v[0])) { DialogWindow.Info(this, "提示", "已有相同名稱的錢包"); return; }
            RefreshAll();
        }

        void DeleteWallet_Click(object sender, RoutedEventArgs e)
        {
            if (WalletList.SelectedItem is not WalletItem { Id: long id } item) return;
            if (db.WalletCount() <= 1) { DialogWindow.Info(this, "提示", "至少要保留一個錢包"); return; }
            int n = db.WalletRecordCount(id);
            if (!DialogWindow.Confirm(this, "刪除錢包",
                    $"刪除「{item.Name}」,並一併刪除其中 {n} 筆記錄?\n此動作無法復原。", "刪除", danger: true))
                return;
            db.DeleteWallet(id);
            selWallet = null;
            RefreshAll();
        }

        // ================= 收支記錄 =================
        string RecType => RecIncome.IsChecked == true ? Database.Income : Database.Expense;

        void LoadCategories()
        {
            var cur = RecCategory.SelectedItem as string;
            var cats = db.Categories(RecType);
            RecCategory.ItemsSource = cats;
            RecCategory.SelectedItem = cats.Contains(cur ?? "") ? cur : cats.FirstOrDefault();
        }

        void RecType_Checked(object sender, RoutedEventArgs e)
        {
            if (RecCategory != null) LoadCategories();
        }

        string Month => MonthBox.Text.Trim(); // yyyy-MM、yyyy(整年)或空白(全部期間)
        string? catFilter; // 從支出圖表點分類過來時,只顯示該分類的支出

        List<RecordRow> FilteredRecords()
        {
            var rows = db.Records(Month, selWallet);
            return catFilter == null ? rows : rows.Where(r => !r.IsIncome && r.Category == catFilter).ToList();
        }

        void RefreshRecords()
        {
            var rows = FilteredRecords();
            RecordGrid.ItemsSource = rows;
            CatChip.Visibility = catFilter == null ? Visibility.Collapsed : Visibility.Visible;
            CatChipText.Text = $"{Database.Expense} · {catFilter}";
            double inc = rows.Where(r => r.IsIncome).Sum(r => r.Amount);
            double exp = rows.Where(r => !r.IsIncome).Sum(r => r.Amount);
            double bal = db.Wallets().Where(w => selWallet == null || w.Id == selWallet).Sum(w => w.Balance);

            var period = Month == "" ? "全部期間" : Month;
            BalanceText.Text = Money(bal);
            BalanceText.Foreground = bal >= 0 ? Brushes.White : (Brush)FindResource("ExpBrush");
            BalanceSub.Text = (WalletList.SelectedItem as WalletItem)?.Name ?? "全部錢包";
            IncomeText.Text = Money(inc);
            ExpenseText.Text = Money(exp);
            NetText.Text = (inc - exp > 0 ? "+" : "") + Money(inc - exp);
            NetText.Foreground = (Brush)FindResource(inc >= exp ? "IncBrush" : "ExpBrush");
            IncomeSub.Text = $"{period} · {rows.Count(r => r.IsIncome)} 筆";
            ExpenseSub.Text = $"{period} · {rows.Count(r => !r.IsIncome)} 筆";
            NetSub.Text = period;
            RecCount.Text = $"共 {rows.Count} 筆";
            RecEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshStats();
        }

        void ClearCatFilter_Click(object sender, RoutedEventArgs e)
        {
            catFilter = null;
            RefreshRecords();
        }

        void ShiftMonth(int delta)
        {
            if (Month.Length == 4 && int.TryParse(Month, out var year)) // 從圖表帶過來的整年期間
            {
                MonthBox.Text = (year + delta).ToString();
                RefreshRecords();
                return;
            }
            var d = DateTime.TryParseExact(Month + "-01", "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out var m) ? m : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            MonthBox.Text = d.AddMonths(delta).ToString("yyyy-MM");
            RefreshRecords();
        }

        void PrevMonth_Click(object sender, RoutedEventArgs e) => ShiftMonth(-1);
        void NextMonth_Click(object sender, RoutedEventArgs e) => ShiftMonth(1);

        void ThisMonth_Click(object sender, RoutedEventArgs e)
        {
            MonthBox.Text = DateTime.Today.ToString("yyyy-MM");
            RefreshRecords();
        }

        void AllPeriod_Click(object sender, RoutedEventArgs e)
        {
            MonthBox.Text = "";
            RefreshRecords();
        }

        void MonthBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { RefreshRecords(); e.Handled = true; }
        }

        void MonthBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => RefreshRecords();

        void RecForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddRecord(); e.Handled = true; }
        }

        void AddRecord_Click(object sender, RoutedEventArgs e) => AddRecord();

        void AddRecord()
        {
            if (RecDate.SelectedDate is not DateTime date ||
                !DialogWindow.TryParseAmount(RecAmount.Text, out var amt) || amt <= 0)
            {
                DialogWindow.Error(this, "無法新增", "請確認日期(YYYY-MM-DD)與金額(需大於 0)。");
                return;
            }
            if (RecWallet.SelectedItem is not Wallet w) { DialogWindow.Error(this, "無法新增", "請選擇錢包。"); return; }
            if (RecCategory.SelectedItem is not string cat)
            {
                DialogWindow.Error(this, "無法新增", "請先在「管理分類」建立分類。");
                return;
            }
            db.AddRecord(date.ToString("yyyy-MM-dd"), RecType, cat, amt, RecNote.Text.Trim(), w.Id);
            RecAmount.Clear();
            RecNote.Clear();
            RecAmount.Focus();
            RefreshSidebar();
            RefreshRecords();
        }

        void EditRecord_Click(object sender, RoutedEventArgs e) => EditRecord();

        void RecordGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (IsOnRow(e)) EditRecord();
        }

        /// <summary>雙擊在資料列上(不是標題列或空白處)。</summary>
        static bool IsOnRow(MouseButtonEventArgs e)
        {
            for (var o = e.OriginalSource as DependencyObject; o != null; o = VisualTreeHelper.GetParent(o))
                if (o is DataGridRow) return true;
            return false;
        }

        const string TypeSep = " · "; // 編輯框的「類型 · 分類」選項,例如「支出 · 餐飲」

        void EditRecord()
        {
            if (RecordGrid.SelectedItems.Count != 1 || RecordGrid.SelectedItem is not RecordRow r)
            {
                DialogWindow.Info(this, "提示", "請先在清單中選取一筆記錄(或直接雙擊那一筆)。");
                return;
            }
            var wallets = db.Wallets();
            var kinds = new List<string>();
            foreach (var type in new[] { Database.Expense, Database.Income })
                kinds.AddRange(db.Categories(type).Select(c => type + TypeSep + c));
            var current = r.Type + TypeSep + r.Category;
            if (!kinds.Contains(current)) kinds.Insert(0, current); // 分類已被刪掉也保留原值
            var v = DialogWindow.Prompt(this, "編輯記錄", "",
            [
                new Field("日期", r.Date, "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("錢包", r.Wallet, Options: wallets.Select(w => w.Name).ToArray()),
                new Field("類型 · 分類", current, Options: kinds.ToArray()),
                new Field("金額", r.Amount.ToString("0.##")),
                new Field("備註(可空白)", r.Note),
            ], vals =>
            {
                if (!DateTime.TryParse(vals[0], out _)) return "日期格式為 YYYY-MM-DD";
                if (!DialogWindow.TryParseAmount(vals[3], out var a) || a <= 0) return "金額需大於 0";
                return null;
            }, "儲存");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[3], out var amt);
            var parts = v[2].Split(TypeSep, 2);
            var wallet = wallets.First(w => w.Name == v[1]);
            db.UpdateRecord(r.Id, NormalizeDate(v[0]), parts[0], parts[1], amt, v[4], wallet.Id);
            RefreshSidebar();
            RefreshRecords();
            RecordGrid.SelectedItem = ((List<RecordRow>)RecordGrid.ItemsSource).FirstOrDefault(x => x.Id == r.Id);
        }

        void DeleteRecords_Click(object sender, RoutedEventArgs e) => DeleteRecords();

        void RecordGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { DeleteRecords(); e.Handled = true; }
        }

        void DeleteRecords()
        {
            var sel = RecordGrid.SelectedItems.Cast<RecordRow>().ToList();
            if (sel.Count == 0) { DialogWindow.Info(this, "提示", "請先在清單中選取要刪除的記錄(可按住 Ctrl 或 Shift 多選)。"); return; }
            if (!DialogWindow.Confirm(this, "刪除記錄", $"確定刪除 {sel.Count} 筆記錄?", "刪除", danger: true)) return;
            db.DeleteRecords(sel.Select(r => r.Id));
            RefreshSidebar();
            RefreshRecords();
        }

        // ================= 匯出 / 匯入 =================
        void Export_Click(object sender, RoutedEventArgs e)
        {
            var period = Month == "" ? "全部期間" : Month;
            var wallet = (WalletList.SelectedItem as WalletItem)?.Name ?? "全部錢包";
            var choice = DialogWindow.Choose(this, "匯出資料", "選擇要匯出的內容:",
            [
                ("", "完整備份(.json)", "所有錢包、收支、欠款、分類與存錢目標,可用「匯入資料」完整還原"),
                ("", "目前篩選的收支記錄(.csv)", $"{period} · {wallet}{(catFilter != null ? $" · {catFilter}" : "")} · 共 {FilteredRecords().Count} 筆,可用 Excel 開啟"),
                ("", "全部收支記錄(.csv)", $"所有期間與錢包 · 共 {db.Records("", null).Count} 筆"),
            ]);
            if (choice == null) return;

            bool json = choice == 0;
            var dlg = new SaveFileDialog
            {
                Filter = json ? "完整備份 (*.json)|*.json" : "CSV 檔案 (*.csv)|*.csv",
                FileName = json ? $"記帳備份_{DateTime.Now:yyyyMMdd}.json"
                         : choice == 1 ? $"記帳_{(Month == "" ? "全部" : Month)}.csv" : "記帳_全部.csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                if (json) DataTransfer.ExportBackup(db, dlg.FileName);
                else DataTransfer.ExportCsv(choice == 1 ? FilteredRecords() : db.Records("", null), dlg.FileName);
            }
            catch (IOException ex) { DialogWindow.Error(this, "匯出失敗", ex.Message); return; }
            DialogWindow.Success(this, "匯出完成", $"已匯出至\n{dlg.FileName}");
        }

        void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "支援的檔案 (*.json;*.csv)|*.json;*.csv|完整備份 (*.json)|*.json|收支記錄 (*.csv)|*.csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                if (Path.GetExtension(dlg.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                    ImportCsv(dlg.FileName);
                else
                    ImportBackup(dlg.FileName);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                DialogWindow.Error(this, "匯入失敗", ex.Message);
            }
        }

        void ImportBackup(string path)
        {
            var (data, s) = DataTransfer.ReadBackup(path);
            var when = s.ExportedAt != "" ? $"(匯出於 {s.ExportedAt})" : "";
            if (!DialogWindow.Confirm(this, "匯入完整備份",
                    $"備份內容{when}:\n錢包 {s.Wallets} 個、收支 {s.Records} 筆、欠款 {s.Debts} 筆、存錢目標 {s.Goals} 個。\n\n" +
                    "匯入後會「取代」目前所有資料。\n匯入前會自動把目前資料另存一份到備份資料夾。", "取代並匯入", danger: true))
                return;
            var backup = DataTransfer.AutoBackup(db);
            db.ReplaceAll(data);
            ReloadEverything();
            DialogWindow.Success(this, "匯入完成", $"資料已還原。\n原本的資料已備份至:\n{backup}");
        }

        void ImportCsv(string path)
        {
            var result = DataTransfer.ReadCsv(path);
            var skipped = result.Errors.Count == 0 ? ""
                : $"\n\n有 {result.Errors.Count} 行無法辨識,將略過:\n" + string.Join("\n", result.Errors.Take(3)) +
                  (result.Errors.Count > 3 ? "\n…" : "");
            if (result.Rows.Count == 0)
            {
                DialogWindow.Error(this, "沒有可匯入的記錄", "檔案中找不到有效的收支記錄。" + skipped);
                return;
            }
            if (!DialogWindow.Confirm(this, "匯入收支記錄",
                    $"將新增 {result.Rows.Count} 筆收支記錄到現有資料(不會刪除任何資料)。\n" +
                    "不存在的錢包與分類會自動建立。" + skipped, "匯入"))
                return;
            db.ImportRecords(result.Rows);
            ReloadEverything();
            DialogWindow.Success(this, "匯入完成", $"已新增 {result.Rows.Count} 筆收支記錄。");
        }

        // ================= 欠款 =================
        void RefreshDebts()
        {
            var all = db.Debts();
            double owe = all.Where(d => !d.Done && d.IOwe).Sum(d => d.Rest);
            double lent = all.Where(d => !d.Done && !d.IOwe).Sum(d => d.Rest);
            var rows = FilterDone.IsChecked == true ? all.Where(d => d.Done)
                     : FilterAll.IsChecked == true ? all
                     : all.Where(d => !d.Done);
            var list = rows.ToList();
            DebtGrid.ItemsSource = list;

            double net = lent - owe;
            DebtNetText.Text = (net > 0 ? "+" : "") + Money(net);
            DebtNetText.Foreground = net >= 0 ? Brushes.White : (Brush)FindResource("ExpBrush");
            DebtNetSub.Text = net > 0 ? "整體而言別人欠你較多" : net < 0 ? "整體而言你欠別人較多" : "收支打平";
            LentText.Text = Money(lent);
            OweText.Text = Money(owe);
            LentSub.Text = $"{all.Count(d => !d.Done && !d.IOwe)} 筆未還";
            int late = all.Count(d => d.Late);
            OweSub.Text = $"{all.Count(d => !d.Done && d.IOwe)} 筆未還" + (late > 0 ? $" · {late} 筆逾期" : "");
            DebtCount.Text = $"共 {list.Count} 筆";
            DebtEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DebtEmptyText.Text = FilterOpen.IsChecked == true ? "沒有未還清的欠款" : "沒有符合的欠款紀錄";
        }

        void DebtFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (DebtGrid != null) RefreshDebts();
        }

        void DebtForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddDebt(); e.Handled = true; }
        }

        void AddDebt_Click(object sender, RoutedEventArgs e) => AddDebt();

        void AddDebt()
        {
            var person = DebtPerson.Text.Trim();
            if (person == "" || DebtDate.SelectedDate is not DateTime date ||
                !DialogWindow.TryParseAmount(DebtAmount.Text, out var amt) || amt <= 0)
            {
                DialogWindow.Error(this, "無法新增", "請確認:對象不可空白、金額需大於 0、日期格式為 YYYY-MM-DD。");
                return;
            }
            var due = DebtDue.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
            db.AddDebt(person, DebtIOwe.IsChecked == true ? Database.IOwe : Database.TheyOwe,
                       amt, date.ToString("yyyy-MM-dd"), due, DebtNote.Text.Trim());
            DebtPerson.Clear();
            DebtAmount.Clear();
            DebtNote.Clear();
            DebtDue.SelectedDate = null;
            DebtPerson.Focus();
            RefreshDebts();
        }

        void PayDebt_Click(object sender, RoutedEventArgs e) => PayDebt();

        void DebtGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DebtGrid.SelectedItems.Count == 1) PayDebt();
        }

        void PayDebt()
        {
            if (DebtGrid.SelectedItems.Count != 1 || DebtGrid.SelectedItem is not DebtRow d)
            {
                DialogWindow.Info(this, "提示", "請先在清單中選取一筆欠款。");
                return;
            }
            if (d.Done) { DialogWindow.Info(this, "提示", "這筆欠款已經還清了。"); return; }
            var v = DialogWindow.Prompt(this, "記錄還款", $"{d.Person}({d.Direction})目前未還 {Money(d.Rest)}",
                [new Field("本次還款金額", "", $"最多 {Money(d.Rest)}")],
                vals =>
                {
                    if (!DialogWindow.TryParseAmount(vals[0], out var a) || a < 0.01) return "請輸入大於 0 的金額";
                    if (a > d.Amount - d.Paid + 0.0001) return "還款金額超過未還金額";
                    return null;
                }, "記錄");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[0], out var amt);
            db.PayDebt(d.Id, amt);
            RefreshDebts();
        }

        void EditDebt_Click(object sender, RoutedEventArgs e)
        {
            if (DebtGrid.SelectedItems.Count != 1 || DebtGrid.SelectedItem is not DebtRow d)
            {
                DialogWindow.Info(this, "提示", "請先在清單中選取一筆欠款。");
                return;
            }
            var v = DialogWindow.Prompt(this, "編輯欠款", "",
            [
                new Field("對象", d.Person),
                new Field("方向", d.Direction, Options: [Database.IOwe, Database.TheyOwe]),
                new Field("金額", d.Amount.ToString("0.##")),
                new Field("已還金額", d.Paid.ToString("0.##")),
                new Field("日期", d.Date, "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("到期日(可空白)", d.Due, "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("備註(可空白)", d.Note),
            ], vals =>
            {
                if (vals[0] == "") return "對象不可空白";
                if (!DialogWindow.TryParseAmount(vals[2], out var a) || a <= 0) return "金額需大於 0";
                if (!DialogWindow.TryParseAmount(vals[3] == "" ? "0" : vals[3], out var p) || p < 0) return "已還金額需為 0 或正數";
                if (p > a + 0.0001) return "已還金額不能超過金額";
                if (!DateTime.TryParse(vals[4], out _)) return "日期格式為 YYYY-MM-DD";
                if (vals[5] != "" && !DateTime.TryParse(vals[5], out _)) return "到期日格式為 YYYY-MM-DD,或留空";
                return null;
            }, "儲存");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[2], out var amt);
            DialogWindow.TryParseAmount(v[3] == "" ? "0" : v[3], out var paid);
            db.UpdateDebt(d.Id, v[0], v[1], amt, paid, NormalizeDate(v[4]), NormalizeDate(v[5]), v[6]);
            RefreshDebts();
            DebtGrid.SelectedItem = ((List<DebtRow>)DebtGrid.ItemsSource).FirstOrDefault(x => x.Id == d.Id);
        }

        List<DebtRow>? SelectedDebts()
        {
            var sel = DebtGrid.SelectedItems.Cast<DebtRow>().ToList();
            if (sel.Count > 0) return sel;
            DialogWindow.Info(this, "提示", "請先在清單中選取欠款(可按住 Ctrl 或 Shift 多選)。");
            return null;
        }

        void SettleDebts_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDebts() is not { } sel) return;
            if (!DialogWindow.Confirm(this, "標記還清", $"將選取的 {sel.Count} 筆標記為已還清?")) return;
            db.SettleDebts(sel.Select(d => d.Id));
            RefreshDebts();
        }

        void DeleteDebts_Click(object sender, RoutedEventArgs e) => DeleteDebts();

        void DebtGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { DeleteDebts(); e.Handled = true; }
        }

        void DeleteDebts()
        {
            if (SelectedDebts() is not { } sel) return;
            if (!DialogWindow.Confirm(this, "刪除欠款", $"確定刪除 {sel.Count} 筆欠款記錄?", "刪除", danger: true)) return;
            db.DeleteDebts(sel.Select(d => d.Id));
            RefreshDebts();
        }

        // ================= 存錢目標 =================
        void RefreshGoals(long? select = null)
        {
            select ??= (GoalList.SelectedItem as GoalRow)?.Id;
            var goals = db.Goals();
            GoalList.ItemsSource = goals;
            GoalList.SelectedItem = goals.FirstOrDefault(g => g.Id == select) ?? goals.FirstOrDefault();

            double saved = goals.Sum(g => g.Saved), target = goals.Sum(g => g.Target);
            double pct = target > 0 ? Math.Clamp(goals.Sum(g => Math.Min(g.Saved, g.Target)) / target, 0, 1) : 0;
            GoalSavedText.Text = Money(saved);
            GoalTotalBar.Value = pct;
            GoalTotalPct.Text = $"整體進度 {pct:P0}";
            GoalTargetText.Text = Money(target);
            GoalTargetSub.Text = $"{goals.Count} 個目標 · 還差 {Money(goals.Sum(g => g.Remaining))}";
            int done = goals.Count(g => g.Done);
            GoalDoneText.Text = $"{done} / {goals.Count}";
            int toPay = goals.Count(g => g.Yearly && g.Late), late = goals.Count(g => !g.Yearly && g.Late);
            GoalDoneSub.Text = toPay > 0 ? $"{toPay} 筆費用該繳了"
                : late > 0 ? $"{late} 個目標已過期"
                : done == goals.Count && done > 0 ? "全部達成,太棒了!" : "繼續加油";
            GoalCount.Text = $"共 {goals.Count} 個";
            GoalEmpty.Visibility = goals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshDeposits();
        }

        void RefreshDeposits()
        {
            if (GoalList.SelectedItem is not GoalRow g)
            {
                DepositList.ItemsSource = null;
                GoalDetailName.Text = "";
                DepositEmpty.Text = GoalList.Items.Count == 0 ? "建立目標後,存入與取出的\n紀錄會顯示在這裡" : "選擇左側的目標查看紀錄";
                DepositEmpty.Visibility = Visibility.Visible;
                return;
            }
            var list = db.Deposits(g.Id);
            DepositList.ItemsSource = list;
            GoalDetailName.Text = $"{g.Name} · {g.Count} 筆";
            DepositEmpty.Text = "還沒有存取紀錄\n按「存入」開始存錢吧";
            DepositEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void GoalList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDeposits();

        static GoalRow GoalOf(object sender) => (GoalRow)((FrameworkElement)sender).Tag;

        /// <summary>目標的輸入欄位:名稱、金額、日期、備註(每年繳費的日期必填)。</summary>
        static (Field[] Fields, Func<string[], string?> Validate) GoalForm(bool yearly, GoalRow? g = null)
        {
            Field[] fields = yearly
            ? [
                new Field("費用名稱", g?.Name ?? "", "例如:汽車牌照稅、燃料稅、汽車保險"),
                new Field("每年金額", g?.Target.ToString("0.##") ?? "", "例如:11230"),
                new Field("下次繳費日", g?.Deadline ?? "", "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("備註(可空白)", g?.Note ?? ""),
            ]
            : [
                new Field("目標名稱", g?.Name ?? "", "例如:日本旅行、緊急預備金"),
                new Field("目標金額", g?.Target.ToString("0.##") ?? "", "例如:50000"),
                new Field("目標日期(可空白)", g?.Deadline ?? "", "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("備註(可空白)", g?.Note ?? ""),
            ];
            return (fields, v =>
            {
                if (v[0] == "") return yearly ? "請輸入費用名稱" : "請輸入目標名稱";
                if (!DialogWindow.TryParseAmount(v[1], out var t) || t <= 0) return yearly ? "每年金額需大於 0" : "目標金額需大於 0";
                if (yearly && v[2] == "") return "請選擇下次繳費日";
                if (v[2] != "" && !DateTime.TryParse(v[2], out _)) return "日期格式為 YYYY-MM-DD" + (yearly ? "" : ",或留空");
                return null;
            });
        }

        static string NormalizeDate(string s) => DateTime.TryParse(s, out var d) ? d.ToString("yyyy-MM-dd") : "";

        void AddGoal_Click(object sender, RoutedEventArgs e)
        {
            var kind = DialogWindow.Choose(this, "新增存錢目標", "要存哪一種?",
            [
                ("", "一次性目標", "存到目標金額就完成,例如日本旅行、緊急預備金、新手機。"),
                ("", "每年繳費", "每年固定要繳的費用,例如汽車牌照稅、燃料稅、保險費。繳費後自動換到下一年。"),
            ]);
            if (kind == null) return;
            bool yearly = kind == 1;
            var (fields, validate) = GoalForm(yearly);
            var v = DialogWindow.Prompt(this, yearly ? "新增每年繳費" : "新增存錢目標",
                yearly ? "設定每年要繳的金額與繳費日,程式會算出每個月該存多少。" : "設定想存到的金額,也可以加上目標日期。",
                fields, validate, "建立");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[1], out var target);
            var id = db.AddGoal(v[0], target, NormalizeDate(v[2]), v[3], yearly ? Database.Yearly : "");
            RefreshGoals(id);
        }

        void GoalEdit_Click(object sender, RoutedEventArgs e)
        {
            var g = GoalOf(sender);
            GoalList.SelectedItem = g;
            var (fields, validate) = GoalForm(g.Yearly, g);
            var v = DialogWindow.Prompt(this, g.Yearly ? "編輯每年繳費" : "編輯目標", "", fields, validate, "儲存");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[1], out var target);
            db.UpdateGoal(g.Id, v[0], target, NormalizeDate(v[2]), v[3]);
            RefreshGoals(g.Id);
        }

        void GoalPay_Click(object sender, RoutedEventArgs e)
        {
            var g = GoalOf(sender);
            GoalList.SelectedItem = g;
            var next = DateTime.TryParse(g.Deadline, out var due) ? due.AddYears(1) : DateTime.Today.AddYears(1);
            var v = DialogWindow.Prompt(this, $"繳費:{g.Name}",
                $"目前已存 {Money(g.Saved)}。繳費金額會從已存金額扣除,下次繳費日改為 {next:yyyy-MM-dd}。",
            [
                new Field("繳費金額", g.Target.ToString("0.##")),
                new Field("繳費日期", DateTime.Today.ToString("yyyy-MM-dd"), "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
            ], vals =>
            {
                if (!DialogWindow.TryParseAmount(vals[0], out var a) || a <= 0) return "請輸入大於 0 的金額";
                if (!DateTime.TryParse(vals[1], out _)) return "日期格式為 YYYY-MM-DD";
                return null;
            }, "已繳費");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[0], out var paid);
            // 已存的不夠時只扣到 0,不足的部分當作從別處付
            var withdraw = Math.Min(paid, Math.Max(g.Saved, 0));
            var year = due != default ? due.Year : DateTime.Today.Year;
            var note = $"{year} 年繳費" + (paid > withdraw + 0.0001 ? $"(實繳 {Money(paid)})" : "");
            db.PayYearly(g.Id, NormalizeDate(v[1]), withdraw, note, next.ToString("yyyy-MM-dd"));
            RefreshGoals(g.Id);
        }

        void GoalDelete_Click(object sender, RoutedEventArgs e)
        {
            var g = GoalOf(sender);
            if (!DialogWindow.Confirm(this, "刪除目標",
                    $"刪除「{g.Name}」以及其中 {g.Count} 筆存取紀錄?\n此動作無法復原。", "刪除", danger: true))
                return;
            db.DeleteGoal(g.Id);
            RefreshGoals();
        }

        void GoalDeposit_Click(object sender, RoutedEventArgs e) => GoalMove(GoalOf(sender), deposit: true);
        void GoalWithdraw_Click(object sender, RoutedEventArgs e) => GoalMove(GoalOf(sender), deposit: false);

        void GoalMove(GoalRow g, bool deposit)
        {
            GoalList.SelectedItem = g;
            if (!deposit && g.Saved <= 0) { DialogWindow.Info(this, "提示", "這個目標目前沒有存款可以取出。"); return; }
            var msg = deposit
                ? $"「{g.Name}」已存 {Money(g.Saved)},還差 {Money(g.Remaining)}"
                : $"「{g.Name}」目前已存 {Money(g.Saved)}";
            var v = DialogWindow.Prompt(this, deposit ? "存入" : "取出", msg,
            [
                new Field("金額", "", deposit && g.Remaining > 0 ? $"還差 {Money(g.Remaining)}" : $"最多 {Money(g.Saved)}"),
                new Field("日期", DateTime.Today.ToString("yyyy-MM-dd"), "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("備註(可空白)", "", deposit ? "例如:薪水提撥" : "例如:臨時急用"),
            ], vals =>
            {
                if (!DialogWindow.TryParseAmount(vals[0], out var a) || a <= 0) return "請輸入大於 0 的金額";
                if (!deposit && a > g.Saved + 0.0001) return "取出金額超過目前已存金額";
                if (!DateTime.TryParse(vals[1], out _)) return "日期格式為 YYYY-MM-DD";
                return null;
            }, deposit ? "存入" : "取出");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[0], out var amt);
            bool wasDone = g.Done;
            db.AddDeposit(g.Id, NormalizeDate(v[1]), deposit ? amt : -amt, v[2]);
            RefreshGoals(g.Id);
            if (deposit && !wasDone && g.Saved + amt >= g.Target - 0.0001)
            {
                if (g.Yearly)
                    DialogWindow.Success(this, "已存足!", $"「{g.Name}」今年的 {Money(g.Target)} 已經存好了。\n繳完費後按「繳費」,就會換到下一年。");
                else
                    DialogWindow.Success(this, "目標達成!", $"恭喜!「{g.Name}」已經存到 {Money(g.Target)} 了。");
            }
        }

        void DepositEdit_Click(object sender, RoutedEventArgs e)
        {
            var d = (DepositRow)((FrameworkElement)sender).Tag;
            var v = DialogWindow.Prompt(this, "編輯存取紀錄", "",
            [
                new Field("類型", d.IsDeposit ? "存入" : "取出", Options: ["存入", "取出"]),
                new Field("金額", Math.Abs(d.Amount).ToString("0.##")),
                new Field("日期", d.Date, "點日曆選擇,或直接輸入 YYYY-MM-DD", IsDate: true),
                new Field("備註(可空白)", d.Note),
            ], vals =>
            {
                if (!DialogWindow.TryParseAmount(vals[1], out var a) || a <= 0) return "請輸入大於 0 的金額";
                if (!DateTime.TryParse(vals[2], out _)) return "日期格式為 YYYY-MM-DD";
                return null;
            }, "儲存");
            if (v == null) return;
            DialogWindow.TryParseAmount(v[1], out var amt);
            db.UpdateDeposit(d.Id, NormalizeDate(v[2]), v[0] == "存入" ? amt : -amt, v[3]);
            RefreshGoals();
        }

        void DepositDelete_Click(object sender, RoutedEventArgs e)
        {
            var d = (DepositRow)((FrameworkElement)sender).Tag;
            if (!DialogWindow.Confirm(this, "刪除紀錄", $"刪除 {d.Date} 的這筆{(d.IsDeposit ? "存入" : "取出")}({d.AmountText})?",
                    "刪除", danger: true))
                return;
            db.DeleteDeposit(d.Id);
            RefreshGoals();
        }

        // ================= 支出圖表 =================
        string statsPeriod = DateTime.Today.ToString("yyyy-MM"); // yyyy-MM 一個月、yyyy 一整年、空白為全部期間

        static DateTime ThisMonth => new(DateTime.Today.Year, DateTime.Today.Month, 1);

        static string PeriodName(string p) => p.Length switch
        {
            0 => "全部期間",
            4 => $"{p} 年",
            _ => DateTime.ParseExact(p, "yyyy-MM", null).ToString("yyyy 年 M 月"),
        };

        void RefreshStats()
        {
            if (StatsView.Visibility != Visibility.Visible) return; // 切到這個分頁時才計算
            var p = statsPeriod;
            bool isYear = p.Length == 4;
            var spent = db.Records(p, selWallet).Where(r => !r.IsIncome).ToList();
            double total = spent.Sum(r => r.Amount);

            StatPeriodText.Text = PeriodName(p);
            StatPrevBtn.IsEnabled = StatNextBtn.IsEnabled = p != "";
            StatPrevBtn.ToolTip = isYear ? "上一年" : "上個月";
            StatNextBtn.ToolTip = isYear ? "下一年" : "下個月";
            StatWalletText.Text = (WalletList.SelectedItem as WalletItem)?.Name ?? "全部錢包";
            StatExpenseText.Text = Money(total);
            StatExpenseSub.Text = $"{PeriodName(p)} · {spent.Count} 筆";

            // 與上一期相比
            var last = isYear ? "去年" : "上個月";
            StatCompareLabel.Text = p == "" ? "與上一期相比" : $"與{last}相比";
            StatCompareText.Foreground = (Brush)FindResource("TextBrush");
            if (p == "")
            {
                StatCompareText.Text = "—";
                StatCompareSub.Text = "選擇月份或年份才能比較";
            }
            else
            {
                double before = db.Records(ShiftPeriod(p, -1), selWallet).Where(r => !r.IsIncome).Sum(r => r.Amount);
                double diff = total - before;
                if (before <= 0)
                {
                    StatCompareText.Text = "—";
                    StatCompareSub.Text = $"{last}沒有支出";
                }
                else
                {
                    StatCompareText.Text = (diff > 0 ? "+" : diff < 0 ? "−" : "") + $"{Math.Abs(diff) / before:P0}";
                    StatCompareText.Foreground = (Brush)FindResource(diff > 0 ? "ExpBrush" : diff < 0 ? "IncBrush" : "TextBrush");
                    StatCompareSub.Text = diff > 0 ? $"比{last}多花 {Money(diff)}"
                                        : diff < 0 ? $"比{last}少花 {Money(-diff)}" : $"跟{last}一樣";
                }
            }

            // 平均每天:本月、今年只算到今天
            var (days, daysNote) = CountDays(p, spent);
            StatAvgText.Text = Money(days > 0 ? total / days : 0);
            StatAvgSub.Text = days > 0 ? $"{daysNote}共 {days} 天" : "這段期間沒有支出";

            var big = spent.MaxBy(r => r.Amount);
            StatMaxText.Text = big == null ? "—" : Money(big.Amount);
            StatMaxSub.Text = big == null ? "這段期間沒有支出"
                : $"{big.Category} · {big.Date}" + (big.Note != "" ? $" · {big.Note}" : "");

            // 分類圓環圖與列表
            var cats = db.Categories(Database.Expense);
            var stats = spent.GroupBy(r => r.Category)
                .Select(g => (Name: g.Key, Amount: g.Sum(r => r.Amount), Count: g.Count()))
                .OrderByDescending(x => x.Amount)
                .Select(x => new CategoryStat(x.Name, x.Amount, x.Count, total > 0 ? x.Amount / total : 0,
                                              ChartPalette.For(x.Name, cats)))
                .ToList();
            Donut.SetData(stats, "總支出", Money(total), $"{stats.Count} 個分類");
            CatLegend.ItemsSource = stats;
            CatEmpty.Visibility = stats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 每月收支:選整年時顯示該年 1~12 月;選月份時顯示近 12 個月,選到更早的月份則以它為最後一個月
            DateTime end = ThisMonth;
            if (isYear) end = new DateTime(int.Parse(p), 12, 1);
            else if (p != "")
            {
                var sel = DateTime.ParseExact(p, "yyyy-MM", null);
                if (sel > end || sel <= end.AddMonths(-12)) end = sel;
            }
            var start = end.AddMonths(-11);
            var totals = db.MonthTotals(selWallet).ToDictionary(m => m.Month);
            var months = Enumerable.Range(0, 12)
                .Select(i => start.AddMonths(i).ToString("yyyy-MM"))
                .Select(m => totals.TryGetValue(m, out var t) ? t : new MonthTotal(m, 0, 0))
                .ToList();
            Bars.SetData(months, isYear || p == "" ? null : p);
            BarTitle.Text = isYear ? $"{p} 年每月收支"
                : end == ThisMonth ? "近 12 個月收支" : $"{start:yyyy/M} – {end:yyyy/M} 收支";
        }

        /// <summary>計算平均用的天數;本月與今年只算到今天,全部期間從第一筆支出算起。</summary>
        static (int Days, string Note) CountDays(string p, List<RecordRow> spent)
        {
            var today = DateTime.Today;
            if (p == "")
            {
                if (spent.Count == 0) return (0, "");
                var first = spent.Select(r => DateTime.TryParse(r.Date, out var d) ? d : today).Min();
                return (Math.Max((today - first).Days + 1, 1), $"從 {first:yyyy-MM-dd} 起");
            }
            if (p.Length == 4)
            {
                int y = int.Parse(p);
                return y == today.Year ? (today.DayOfYear, "今年到今天") : (DateTime.IsLeapYear(y) ? 366 : 365, "");
            }
            var m = DateTime.ParseExact(p, "yyyy-MM", null);
            return m == ThisMonth ? (today.Day, "本月到今天") : (DateTime.DaysInMonth(m.Year, m.Month), "");
        }

        /// <summary>期間往前或往後移;整年移一年,月份移一個月。</summary>
        static string ShiftPeriod(string p, int delta) => p.Length == 4
            ? (int.Parse(p) + delta).ToString()
            : DateTime.ParseExact(p, "yyyy-MM", null).AddMonths(delta).ToString("yyyy-MM");

        void StatPrev_Click(object sender, RoutedEventArgs e) { statsPeriod = ShiftPeriod(statsPeriod, -1); RefreshStats(); }
        void StatNext_Click(object sender, RoutedEventArgs e) { statsPeriod = ShiftPeriod(statsPeriod, 1); RefreshStats(); }
        void StatThisMonth_Click(object sender, RoutedEventArgs e) { statsPeriod = ThisMonth.ToString("yyyy-MM"); RefreshStats(); }
        void StatThisYear_Click(object sender, RoutedEventArgs e) { statsPeriod = DateTime.Today.Year.ToString(); RefreshStats(); }
        void StatAll_Click(object sender, RoutedEventArgs e) { statsPeriod = ""; RefreshStats(); }

        static CategoryStat StatOf(object sender) => (CategoryStat)((FrameworkElement)sender).Tag;

        void CatLegend_Click(object sender, RoutedEventArgs e) => ShowCategoryRecords(StatOf(sender).Name);
        void CatLegend_MouseEnter(object sender, MouseEventArgs e) => Donut.Highlight(StatOf(sender).Name);
        void CatLegend_MouseLeave(object sender, MouseEventArgs e) => Donut.Highlight(null);

        /// <summary>滑過圓環圖某一塊時,列表只留該分類清楚,其他淡化。</summary>
        void HighlightLegend(string? name)
        {
            foreach (var s in CatLegend.Items.Cast<CategoryStat>())
                if (CatLegend.ItemContainerGenerator.ContainerFromItem(s) is UIElement el)
                    el.Opacity = name == null || s.Name == name ? 1 : 0.4;
        }

        /// <summary>切到收支記錄,只顯示目前圖表期間、該分類的支出。</summary>
        void ShowCategoryRecords(string category)
        {
            catFilter = category;
            MonthBox.Text = statsPeriod;
            TabRecords.IsChecked = true;
            RefreshRecords();
        }

        // ================= 其他 =================
        void ManageCategories_Click(object sender, RoutedEventArgs e)
        {
            var w = new CategoryWindow(db) { Owner = this };
            SetDim(true);
            try { w.ShowDialog(); }
            finally { SetDim(false); }
            if (w.Changed) LoadCategories();
        }

        void About_Click(object sender, RoutedEventArgs e) =>
            DialogWindow.Info(this, "關於簡易記帳",
                $"版本 {Updater.CurrentVersion}\n資料位置:{Path.Combine(Database.DataDir, "ledger.db")}");

        async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdate(silent: false);

        async Task CheckUpdate(bool silent)
        {
            UpdateInfo? info;
            try { info = await Updater.CheckAsync(); }
            catch (Exception ex)
            {
                if (!silent) DialogWindow.Error(this, "檢查更新", Updater.Explain(ex));
                return;
            }
            if (info == null)
            {
                if (!silent) DialogWindow.Success(this, "檢查更新", $"目前已是最新版本(v{Updater.CurrentVersion})。");
                return;
            }
            var notes = info.Notes.Length > 400 ? info.Notes[..400] : info.Notes;
            if (notes == "") notes = "(無說明)";
            var head = $"有新版本 {info.Version} 可用(目前 v{Updater.CurrentVersion})。\n\n{notes}\n\n";

            // 安裝版且 Release 附有安裝包:直接下載並升級;免安裝版或開發版:前往下載頁面
            if (!Updater.IsInstalled || info.PackageUrl == null)
            {
                if (DialogWindow.Confirm(this, "發現新版本", head + "要前往下載頁面嗎?", "前往下載"))
                    Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
                return;
            }

            if (!DialogWindow.Confirm(this, "發現新版本",
                    head + "要現在更新嗎?下載完成後會開啟安裝視窗,完成後請重新開啟程式。\n記帳資料不會受影響。", "立即更新"))
                return;
            try
            {
                VersionText.Text = "下載更新中…";
                await Updater.DownloadAndLaunchAsync(info, p => Dispatcher.Invoke(() => VersionText.Text = $"下載更新中… {p}%"));
                Application.Current.Shutdown(); // 結束本程式,讓 ClickOnce 能覆蓋舊版
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
            {
                VersionText.Text = $"v{Updater.CurrentVersion}";
                DialogWindow.Error(this, "更新失敗", ex.Message);
            }
        }
    }
}
