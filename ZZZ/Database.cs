using System.IO;
using Microsoft.Data.Sqlite;

namespace ZZZ
{
    /// <param name="Kind">空字串為一般錢包;<see cref="Database.Savings"/> 為存錢用錢包(不算進流動資金);
    /// <see cref="Database.CreditCard"/> 為信用卡(餘額為負代表欠款)</param>
    /// <param name="ClosingDay">信用卡每月結帳日(1–31,月份天數不夠時為月底)</param>
    /// <param name="DueDay">信用卡每月繳款日</param>
    /// <param name="PayWalletId">繳卡費預設從哪個錢包付</param>
    public record Wallet(long Id, string Name, double Balance, string Kind = "", double Limit = 0,
                         int ClosingDay = 0, int DueDay = 0, long? PayWalletId = null)
    {
        public bool IsCard => Kind == Database.CreditCard;
        public bool IsSavings => Kind == Database.Savings;
        public double Owed => Math.Max(-Balance, 0);
    }

    /// <summary>
    /// 信用卡帳單。結帳日(含)以前刷的算本期,之後刷的是未出帳;
    /// 結帳後從其他錢包轉進來的錢(繳卡費)先抵本期帳單。
    /// </summary>
    /// <param name="Statement">結帳當天的欠款</param>
    /// <param name="Remaining">本期還要繳的金額</param>
    /// <param name="Owed">目前總欠款</param>
    public record CardBill(DateTime Closing, DateTime Due, double Statement, double Remaining, double Owed, double Limit)
    {
        public double Paid => Math.Max(Statement - Remaining, 0);
        public double Unbilled => Math.Max(Owed - Remaining, 0);
        public int DaysLeft => (int)(Due - DateTime.Today).TotalDays;
        public bool Settled => Remaining <= 0.0001;
        public bool Late => !Settled && DaysLeft < 0;

        public static DateTime DayIn(int year, int month, int day) =>
            new(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)));

        /// <summary>today 當天或之前最近的一次結帳日。</summary>
        public static DateTime LastClosing(int closingDay, DateTime today)
        {
            var c = DayIn(today.Year, today.Month, closingDay);
            if (c <= today) return c;
            var prev = today.AddMonths(-1);
            return DayIn(prev.Year, prev.Month, closingDay);
        }

        /// <summary>結帳日之後的第一個繳款日(繳款日比結帳日小就是下個月)。</summary>
        public static DateTime DueAfter(DateTime closing, int dueDay)
        {
            var d = DayIn(closing.Year, closing.Month, dueDay);
            if (d > closing) return d;
            var next = closing.AddMonths(1);
            return DayIn(next.Year, next.Month, dueDay);
        }

        /// <summary>側欄錢包名稱下方那一行。</summary>
        public string Short => Owed <= 0.0001 ? "沒有欠款"
            : Late ? $"逾期未繳 {Remaining:N0}"
            : Settled ? "本期已繳清"
            : $"應繳 {Remaining:N0} · {Due:M/d} 前";

        /// <summary>收支記錄上方大卡片的說明行。</summary>
        public string Detail => Owed <= 0.0001 ? "目前沒有欠款"
            : Late ? $"已過繳款日 {-DaysLeft} 天({Due:M/d})"
            : Settled ? $"本期已繳清 · 未出帳 {Unbilled:N0}"
            : $"{Due:M/d} 前繳(剩 {DaysLeft} 天)" + (Unbilled > 0.0001 ? $" · 未出帳 {Unbilled:N0}" : "");

        public string Tip
        {
            get
            {
                var lines = new List<string>
                {
                    $"本期帳單 {Statement:N0}({Closing:M/d} 結帳)",
                    $"已繳 {Paid:N0},還要繳 {Remaining:N0},繳款日 {Due:M/d}",
                    $"未出帳 {Unbilled:N0},目前總欠款 {Owed:N0}",
                };
                if (Limit > 0) lines.Add($"信用額度 {Limit:N0},可用 {Limit - Owed:N0}");
                return string.Join("\n", lines);
            }
        }
    }

    public record RecordRow(long Id, string Date, string Wallet, string Type,
                            string Category, double Amount, string Note)
    {
        public bool IsIncome => Type == Database.Income;
        public virtual bool IsTransfer => false;
        public virtual string AmountText => (IsIncome ? "+" : "−") + Amount.ToString("N0");
    }

    /// <summary>錢包之間的轉帳(轉移資金、繳卡費),和收支記錄列在同一個清單,但不算收入或支出。</summary>
    /// <param name="Sign">只看某個錢包時,轉出為「−」、轉入為「+」;看全部錢包時不加正負號</param>
    /// <param name="ToCard">轉進信用卡,也就是繳卡費</param>
    public record TransferRow(long Id, string Date, long FromId, string From, long ToId, string To,
                              double Amount, string Note, string Sign, bool ToCard)
        : RecordRow(Id, Date, $"{From} → {To}", Database.Transfer, ToCard ? Database.CardPayment : Database.MoveFunds, Amount, Note)
    {
        public override bool IsTransfer => true;
        public override string AmountText => Sign + Amount.ToString("N0");
    }

    public record DebtRow(long Id, string Person, string Direction, double Amount, double Paid,
                          string Date, string Due, string Note)
    {
        public double Rest => Math.Max(Amount - Paid, 0);
        public bool Done => Amount - Paid <= 0.0001;
        public bool Late => !Done && Due != "" && string.CompareOrdinal(Due, DateTime.Today.ToString("yyyy-MM-dd")) < 0;
        public bool IOwe => Direction == Database.IOwe;
        public string Status => Done ? "已還清" : Late ? "逾期" : "未還";
        public double Progress => Amount <= 0 ? 0 : Math.Clamp(Paid / Amount, 0, 1);
        public string ProgressText => $"已還 {Progress:P0}";
    }

    /// <param name="Repeat">空字串為一次性目標;<see cref="Database.Yearly"/> 為每年繳費(Deadline 是下次繳費日)</param>
    /// <param name="WalletId">
    /// 這個目標的錢存在哪個錢包。存入/取出是把該錢包的錢分配給目標,同一錢包可放多個目標,
    /// 但分配總額不能超過錢包餘額。
    /// </param>
    public record GoalRow(long Id, string Name, double Target, double Saved, string Deadline, string Note, int Count,
                          string Repeat = "", long? WalletId = null, string WalletName = "")
    {
        public bool Yearly => Repeat == Database.Yearly;
        public bool Linked => WalletId != null;
        public double Progress => Target <= 0 ? 0 : Math.Clamp(Saved / Target, 0, 1);
        public string PercentText => $"{Progress:P0}";
        public double Remaining => Math.Max(Target - Saved, 0);
        public bool Done => Saved >= Target - 0.0001;
        public int? DaysLeft => DateTime.TryParse(Deadline, out var d) ? (int)(d.Date - DateTime.Today).TotalDays : null;
        /// <summary>一次性目標:過了目標日還沒存到;每年繳費:過了繳費日還沒按「繳費」。</summary>
        public bool Late => (Yearly || !Done) && DaysLeft < 0;
        public string Initial => Name.Length > 0 ? Name[..1] : "?";
        public string SavedText => Saved.ToString("N0");
        public string TargetText => "/ " + Target.ToString("N0");
        public string Status => Yearly
            ? Late ? "該繳費了" : Done ? "已存足" : "每年繳費"
            : Done ? "已達成" : Late ? "已過期" : "進行中";
        public string DeadlineText => Yearly && DateTime.TryParse(Deadline, out var d)
            ? $"每年 {d.Month}/{d.Day} 繳費"
            : Deadline != "" ? $"目標日 {Deadline}" : "未設定目標日期";
        /// <summary>卡片名稱下方那一行:連結的錢包、每年繳費一律顯示繳費日,一次性目標有備註就顯示備註。</summary>
        public string SubText
        {
            get
            {
                var text = Yearly ? (Note != "" ? $"{DeadlineText} · {Note}" : DeadlineText)
                                  : Note != "" ? Note : DeadlineText;
                if (!Linked) return text;
                return Deadline == "" && Note == "" ? $"存在「{WalletName}」" : $"存在「{WalletName}」 · {text}";
            }
        }

        /// <summary>例如「還差 38,000 · 剩 120 天 · 每月約存 9,500」</summary>
        public string Hint
        {
            get
            {
                if (Yearly && Late) return $"繳費日已過 {-DaysLeft} 天,繳完請按「繳費」";
                if (Yearly && Done) return DaysLeft is int left ? $"今年的錢已存足 · {(left == 0 ? "今天繳費" : $"{left} 天後繳費")}" : "今年的錢已存足";
                if (Done) return Saved > Target ? $"超出目標 {Saved - Target:N0}" : "恭喜!目標已達成";
                var parts = new List<string> { $"還差 {Remaining:N0}" };
                if (DaysLeft is int days)
                {
                    if (days < 0) parts.Add($"已過期 {-days} 天");
                    else
                    {
                        parts.Add(days == 0 ? "今天到期" : $"剩 {days} 天");
                        var months = Math.Max(1, Math.Ceiling(days / 30.44));
                        parts.Add($"每月約存 {Math.Ceiling(Remaining / months):N0}");
                    }
                }
                return string.Join(" · ", parts);
            }
        }
    }

    /// <param name="Month">yyyy-MM</param>
    public record MonthTotal(string Month, double Income, double Expense)
    {
        public double Net => Income - Expense;
    }

    public record DepositRow(long Id, string Date, double Amount, string Note)
    {
        public bool IsDeposit => Amount >= 0;
        public string AmountText => (IsDeposit ? "+" : "−") + Math.Abs(Amount).ToString("N0");
        public string Title => Note != "" ? Note : IsDeposit ? "存入" : "取出";
    }

    /// <summary>與 Python 版相同的資料結構與位置(%APPDATA%\Ledger\ledger.db),舊資料可直接沿用。</summary>
    public class Database : IDisposable
    {
        public const string Income = "收入", Expense = "支出";
        public const string IOwe = "我欠別人", TheyOwe = "別人欠我";
        public const string Yearly = "每年"; // goals.repeat
        public const string CreditCard = "信用卡"; // wallets.kind
        public const string Savings = "存錢"; // wallets.kind:存錢用錢包,不算進流動資金
        public const string Transfer = "轉帳", CardPayment = "繳卡費", MoveFunds = "轉移資金"; // 清單中轉帳列的類型、分類

        static readonly string[] DefaultExpense = ["餐飲", "交通", "購物", "居住", "娛樂", "醫療", "其他"];
        static readonly string[] DefaultIncome = ["薪資", "獎金", "投資", "其他"];

        // LEDGER_DATA_DIR 可指定其他資料夾(測試用)
        public static string DataDir { get; } = Environment.GetEnvironmentVariable("LEDGER_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ledger");

        readonly SqliteConnection db;
        SqliteTransaction? tx;

        public Database()
        {
            Directory.CreateDirectory(DataDir);
            db = new SqliteConnection($"Data Source={Path.Combine(DataDir, "ledger.db")}");
            db.Open();
            Init();
        }

        public void Dispose() => db.Dispose();

        SqliteCommand Cmd(string sql, params object?[] args)
        {
            var c = db.CreateCommand();
            c.CommandText = sql;
            c.Transaction = tx;
            for (int i = 0; i < args.Length; i++)
                c.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
            return c;
        }

        void InTransaction(Action work)
        {
            tx = db.BeginTransaction();
            try { work(); tx.Commit(); }
            finally { tx.Dispose(); tx = null; }
        }

        int Exec(string sql, params object?[] args)
        {
            using var c = Cmd(sql, args);
            return c.ExecuteNonQuery();
        }

        object? Scalar(string sql, params object?[] args)
        {
            using var c = Cmd(sql, args);
            return c.ExecuteScalar();
        }

        List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args)
        {
            using var c = Cmd(sql, args);
            using var r = c.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }

        static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)) ?? "";
        static double Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i));

        void Init()
        {
            Exec("CREATE TABLE IF NOT EXISTS records(id INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT, type TEXT," +
                 "category TEXT, amount REAL, note TEXT)");
            Exec("CREATE TABLE IF NOT EXISTS categories(type TEXT, name TEXT, UNIQUE(type, name))");
            Exec("CREATE TABLE IF NOT EXISTS wallets(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT UNIQUE," +
                 "initial REAL DEFAULT 0)");
            Exec("CREATE TABLE IF NOT EXISTS debts(id INTEGER PRIMARY KEY AUTOINCREMENT, person TEXT, direction TEXT," +
                 "amount REAL, paid REAL DEFAULT 0, date TEXT, due TEXT, note TEXT)");
            Exec("CREATE TABLE IF NOT EXISTS goals(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, target REAL," +
                 "deadline TEXT, note TEXT, created TEXT)");
            Exec("CREATE TABLE IF NOT EXISTS goal_deposits(id INTEGER PRIMARY KEY AUTOINCREMENT, goal_id INTEGER," +
                 "date TEXT, amount REAL, note TEXT)");
            if (!Columns("records").Contains("wallet_id")) // 舊版資料升級:全部歸入第一個錢包
                Exec("ALTER TABLE records ADD COLUMN wallet_id INTEGER DEFAULT 1");
            if (!Columns("wallets").Contains("sort")) // 側欄錢包的排列順序
                Exec("ALTER TABLE wallets ADD COLUMN sort INTEGER DEFAULT 0");
            if (!Columns("goals").Contains("repeat")) // 每年繳費的目標
                Exec("ALTER TABLE goals ADD COLUMN repeat TEXT DEFAULT ''");
            if (!Columns("goals").Contains("wallet_id")) // 目標的錢存在哪個錢包(NULL 為不指定)
                Exec("ALTER TABLE goals ADD COLUMN wallet_id INTEGER");
            if (!Columns("wallets").Contains("kind")) // 信用卡
            {
                Exec("ALTER TABLE wallets ADD COLUMN kind TEXT DEFAULT ''");
                Exec("ALTER TABLE wallets ADD COLUMN credit_limit REAL DEFAULT 0");
                Exec("ALTER TABLE wallets ADD COLUMN closing_day INTEGER DEFAULT 0");
                Exec("ALTER TABLE wallets ADD COLUMN due_day INTEGER DEFAULT 0");
                Exec("ALTER TABLE wallets ADD COLUMN pay_wallet_id INTEGER");
            }
            Exec("CREATE TABLE IF NOT EXISTS transfers(id INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT," +
                 "from_wallet INTEGER, to_wallet INTEGER, amount REAL, note TEXT)");
            if (Convert.ToInt64(Scalar("PRAGMA user_version")) < 1) ConvertLinkedGoals();
            EnsureDefaults();
        }

        /// <summary>
        /// v1.0.9 的連結目標是「已存金額 = 錢包餘額」,之後改成分配制(已存金額 = 存取紀錄)。
        /// 升級時補一筆存入,讓已存金額維持升級前看到的數字。只執行一次(PRAGMA user_version)。
        /// </summary>
        void ConvertLinkedGoals() => InTransaction(() =>
        {
            var linked = Query(
                "SELECT g.id, w.initial + COALESCE((SELECT SUM(CASE type WHEN '收入' THEN amount ELSE -amount END) " +
                "FROM records WHERE wallet_id = w.id), 0), COALESCE((SELECT SUM(amount) FROM goal_deposits WHERE goal_id = g.id), 0) " +
                "FROM goals g JOIN wallets w ON w.id = g.wallet_id",
                r => (Id: r.GetInt64(0), Balance: Num(r, 1), Saved: Num(r, 2)));
            foreach (var g in linked)
            {
                double diff = Math.Max(g.Balance, 0) - g.Saved;
                if (Math.Abs(diff) > 0.0001) AddDeposit(g.Id, DateTime.Today.ToString("yyyy-MM-dd"), diff, "連結錢包時的餘額");
            }
            Exec("PRAGMA user_version = 1");
        });

        List<string> Columns(string table) => Query($"PRAGMA table_info({table})", r => r.GetString(1));

        void EnsureDefaults()
        {
            if (Scalar("SELECT 1 FROM wallets") == null)
                Exec("INSERT INTO wallets(name, initial) VALUES('現金', 0)");
            if (Scalar("SELECT 1 FROM categories") == null)
            {
                foreach (var n in DefaultExpense) Exec("INSERT INTO categories VALUES(@p0,@p1)", Expense, n);
                foreach (var n in DefaultIncome) Exec("INSERT INTO categories VALUES(@p0,@p1)", Income, n);
            }
        }

        // ---------- 分類 ----------
        public List<string> Categories(string type) =>
            Query("SELECT name FROM categories WHERE type=@p0 ORDER BY rowid", r => r.GetString(0), type);

        public bool AddCategory(string type, string name)
        {
            try { Exec("INSERT INTO categories VALUES(@p0,@p1)", type, name); return true; }
            catch (SqliteException) { return false; }
        }

        public void DeleteCategory(string type, string name) =>
            Exec("DELETE FROM categories WHERE type=@p0 AND name=@p1", type, name);

        // ---------- 錢包 ----------
        // 錢包餘額 = 初始金額 + 收入 − 支出 + 轉入 − 轉出;until 有值時只算到那一天(含)
        static string BalanceSql(string until) =>
            "w.initial + COALESCE((SELECT SUM(CASE type WHEN '收入' THEN amount ELSE -amount END) FROM records " +
            $"WHERE wallet_id = w.id{until}), 0) + COALESCE((SELECT SUM(amount) FROM transfers WHERE to_wallet = w.id{until}), 0) " +
            $"- COALESCE((SELECT SUM(amount) FROM transfers WHERE from_wallet = w.id{until}), 0)";

        public List<Wallet> Wallets() => Query(
            $"SELECT w.id, w.name, {BalanceSql("")}, w.kind, w.credit_limit, w.closing_day, w.due_day, w.pay_wallet_id " +
            "FROM wallets w ORDER BY w.sort, w.id",
            r => new Wallet(r.GetInt64(0), Str(r, 1), Num(r, 2), Str(r, 3), Num(r, 4), (int)Num(r, 5), (int)Num(r, 6),
                            r.IsDBNull(7) ? null : r.GetInt64(7)));

        /// <summary>信用卡目前這一期的帳單。</summary>
        public CardBill Bill(Wallet card)
        {
            var closing = CardBill.LastClosing(card.ClosingDay, DateTime.Today);
            var c = closing.ToString("yyyy-MM-dd");
            var balance = Convert.ToDouble(Scalar($"SELECT {BalanceSql(" AND date <= @p1")} FROM wallets w WHERE w.id = @p0", card.Id, c));
            var paid = Convert.ToDouble(Scalar("SELECT COALESCE(SUM(amount), 0) FROM transfers WHERE to_wallet = @p0 AND date > @p1", card.Id, c));
            double statement = Math.Max(-balance, 0);
            return new CardBill(closing, CardBill.DueAfter(closing, card.DueDay), statement,
                                Math.Clamp(statement - paid, 0, card.Owed), card.Owed, card.Limit);
        }

        /// <returns>新錢包 id;名稱重複時回傳 null</returns>
        /// <param name="kind">空字串為一般錢包;<see cref="Savings"/> 為存錢用錢包</param>
        public long? AddWallet(string name, double initial, string kind = "")
        {
            try
            {
                Exec($"INSERT INTO wallets(name, initial, sort, kind) VALUES(@p0,@p1,{NextWalletSort},@p2)", name, initial, kind);
                return (long)Scalar("SELECT last_insert_rowid()")!;
            }
            catch (SqliteException) { return null; }
        }

        // 新錢包排在最後
        const string NextWalletSort = "(SELECT COALESCE(MAX(sort), 0) + 1 FROM wallets)";

        /// <summary>依傳入的順序重新排列錢包(側欄拖曳)。</summary>
        public void ReorderWallets(IReadOnlyList<long> ids) => InTransaction(() =>
        {
            for (int i = 0; i < ids.Count; i++) Exec("UPDATE wallets SET sort=@p0 WHERE id=@p1", i + 1, ids[i]);
        });

        public bool RenameWallet(long id, string name)
        {
            try { Exec("UPDATE wallets SET name=@p0 WHERE id=@p1", name, id); return true; }
            catch (SqliteException) { return false; }
        }

        /// <returns>新信用卡的 id;名稱重複時回傳 null</returns>
        /// <param name="owed">目前還沒繳的金額(存成負的初始餘額)</param>
        public long? AddCard(string name, double owed, double limit, int closingDay, int dueDay, long payWalletId)
        {
            try
            {
                Exec("INSERT INTO wallets(name, initial, sort, kind, credit_limit, closing_day, due_day, pay_wallet_id) " +
                     $"VALUES(@p0,@p1,{NextWalletSort},@p2,@p3,@p4,@p5,@p6)", name, -owed, CreditCard, limit, closingDay, dueDay, payWalletId);
                return (long)Scalar("SELECT last_insert_rowid()")!;
            }
            catch (SqliteException) { return null; }
        }

        /// <summary>改名稱與信用卡設定;kind 為空字串時是一般錢包。名稱重複時回傳 false。</summary>
        public bool UpdateWallet(long id, string name, string kind, double limit, int closingDay, int dueDay, long? payWalletId)
        {
            try
            {
                Exec("UPDATE wallets SET name=@p0, kind=@p1, credit_limit=@p2, closing_day=@p3, due_day=@p4, pay_wallet_id=@p5 WHERE id=@p6",
                     name, kind, limit, closingDay, dueDay, payWalletId, id);
                return true;
            }
            catch (SqliteException) { return false; }
        }

        public int WalletCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM wallets"));

        public int WalletRecordCount(long id) =>
            Convert.ToInt32(Scalar("SELECT COUNT(*) FROM records WHERE wallet_id=@p0", id));

        public int WalletTransferCount(long id) =>
            Convert.ToInt32(Scalar("SELECT COUNT(*) FROM transfers WHERE from_wallet=@p0 OR to_wallet=@p0", id));

        /// <summary>
        /// 刪除錢包與其中的記錄。和其他錢包之間的轉帳改成對方錢包的收支(分類「其他」),
        /// 讓對方的餘額不變:例如刪掉信用卡,從銀行繳過的卡費會變成銀行的支出。
        /// </summary>
        public void DeleteWallet(long id) => InTransaction(() =>
        {
            var names = Query("SELECT id, name FROM wallets", r => (Id: r.GetInt64(0), Name: Str(r, 1))).ToDictionary(x => x.Id, x => x.Name);
            bool card = Convert.ToString(Scalar("SELECT kind FROM wallets WHERE id=@p0", id)) == CreditCard;
            var transfers = Query("SELECT date, from_wallet, to_wallet, amount, note FROM transfers WHERE from_wallet=@p0 OR to_wallet=@p0",
                r => (Date: Str(r, 0), From: r.GetInt64(1), To: r.GetInt64(2), Amount: Num(r, 3), Note: Str(r, 4)), id);
            foreach (var t in transfers)
            {
                bool outgoing = t.To == id; // 對方把錢轉進被刪的錢包 → 對方的支出
                long other = outgoing ? t.From : t.To;
                if (other == id || !names.ContainsKey(other)) continue;
                var note = !outgoing ? $"由「{names[id]}」轉入" : card ? $"繳「{names[id]}」卡費" : $"轉到「{names[id]}」";
                AddRecord(t.Date, outgoing ? Expense : Income, "其他", t.Amount, t.Note != "" ? $"{note} · {t.Note}" : note, other);
            }
            Exec("DELETE FROM transfers WHERE from_wallet=@p0 OR to_wallet=@p0", id);
            Exec("DELETE FROM records WHERE wallet_id=@p0", id);
            Exec("UPDATE goals SET wallet_id=NULL WHERE wallet_id=@p0", id); // 連結的目標改回手動存入
            Exec("UPDATE wallets SET pay_wallet_id=NULL WHERE pay_wallet_id=@p0", id);
            Exec("DELETE FROM wallets WHERE id=@p0", id);
        });

        // ---------- 轉帳(轉移資金、繳卡費) ----------
        /// <param name="walletId">只列出這個錢包轉出或轉入的;null 為全部</param>
        public List<TransferRow> Transfers(string month, long? walletId) => Query(
            "SELECT t.id, t.date, t.from_wallet, f.name, t.to_wallet, w.name, t.amount, t.note, w.kind FROM transfers t " +
            "LEFT JOIN wallets f ON f.id = t.from_wallet LEFT JOIN wallets w ON w.id = t.to_wallet " +
            "WHERE t.date LIKE @p0" + (walletId != null ? " AND (t.from_wallet = @p1 OR t.to_wallet = @p1)" : "") +
            " ORDER BY t.date DESC, t.id DESC",
            r =>
            {
                long from = r.GetInt64(2), to = r.GetInt64(4);
                var sign = walletId == null ? "" : walletId == from ? "−" : "+";
                return new TransferRow(r.GetInt64(0), Str(r, 1), from, Str(r, 3), to, Str(r, 5), Num(r, 6), Str(r, 7), sign,
                                       Str(r, 8) == CreditCard);
            },
            walletId != null ? new object?[] { month + "%", walletId } : [month + "%"]);

        public void AddTransfer(string date, long from, long to, double amount, string note) =>
            Exec("INSERT INTO transfers(date, from_wallet, to_wallet, amount, note) VALUES(@p0,@p1,@p2,@p3,@p4)",
                 date, from, to, amount, note);

        public void UpdateTransfer(long id, string date, long from, long to, double amount, string note) =>
            Exec("UPDATE transfers SET date=@p0, from_wallet=@p1, to_wallet=@p2, amount=@p3, note=@p4 WHERE id=@p5",
                 date, from, to, amount, note, id);

        // ---------- 收支記錄 ----------
        public List<RecordRow> Records(string month, long? walletId)
        {
            var where = new List<string>();
            var args = new List<object?>();
            if (month != "") { where.Add($"r.date LIKE @p{args.Count}"); args.Add(month + "%"); }
            if (walletId != null) { where.Add($"r.wallet_id=@p{args.Count}"); args.Add(walletId); }
            var sql = "SELECT r.id, r.date, w.name, r.type, r.category, r.amount, r.note " +
                      "FROM records r LEFT JOIN wallets w ON w.id = r.wallet_id";
            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
            return Query(sql + " ORDER BY r.date DESC, r.id DESC",
                r => new RecordRow(r.GetInt64(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Num(r, 5), Str(r, 6)),
                args.ToArray());
        }

        /// <summary>每個月的收入、支出合計(圖表用),只列出有記錄的月份。</summary>
        public List<MonthTotal> MonthTotals(long? walletId) => Query(
            "SELECT substr(date, 1, 7) m, SUM(CASE type WHEN '收入' THEN amount ELSE 0 END), " +
            "SUM(CASE type WHEN '收入' THEN 0 ELSE amount END) FROM records" +
            (walletId != null ? " WHERE wallet_id=@p0" : "") + " GROUP BY m ORDER BY m",
            r => new MonthTotal(Str(r, 0), Num(r, 1), Num(r, 2)),
            walletId != null ? new object?[] { walletId } : []);

        public void AddRecord(string date, string type, string category, double amount, string note, long walletId) =>
            Exec("INSERT INTO records(date,type,category,amount,note,wallet_id) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",
                 date, type, category, amount, note, walletId);

        public void UpdateRecord(long id, string date, string type, string category, double amount, string note, long walletId) =>
            Exec("UPDATE records SET date=@p0, type=@p1, category=@p2, amount=@p3, note=@p4, wallet_id=@p5 WHERE id=@p6",
                 date, type, category, amount, note, walletId, id);

        /// <param name="transferIds">一起刪除的轉帳(清單裡的繳卡費列)</param>
        public void DeleteRecords(IEnumerable<long> ids, IEnumerable<long>? transferIds = null) => InTransaction(() =>
        {
            foreach (var id in ids) Exec("DELETE FROM records WHERE id=@p0", id);
            foreach (var id in transferIds ?? []) Exec("DELETE FROM transfers WHERE id=@p0", id);
        });

        // ---------- 欠款 ----------
        public List<DebtRow> Debts() => Query(
            "SELECT id,person,direction,amount,paid,date,due,note FROM debts ORDER BY date DESC, id DESC",
            r => new DebtRow(r.GetInt64(0), Str(r, 1), Str(r, 2), Num(r, 3), Num(r, 4), Str(r, 5), Str(r, 6), Str(r, 7)));

        public void AddDebt(string person, string direction, double amount, string date, string due, string note) =>
            Exec("INSERT INTO debts(person,direction,amount,paid,date,due,note) VALUES(@p0,@p1,@p2,0,@p3,@p4,@p5)",
                 person, direction, amount, date, due, note);

        public void UpdateDebt(long id, string person, string direction, double amount, double paid,
                               string date, string due, string note) =>
            Exec("UPDATE debts SET person=@p0, direction=@p1, amount=@p2, paid=@p3, date=@p4, due=@p5, note=@p6 WHERE id=@p7",
                 person, direction, amount, paid, date, due, note, id);

        public void PayDebt(long id, double amount) => Exec("UPDATE debts SET paid = paid + @p0 WHERE id=@p1", amount, id);

        public void SettleDebts(IEnumerable<long> ids) =>
            InTransaction(() => { foreach (var id in ids) Exec("UPDATE debts SET paid = amount WHERE id=@p0", id); });

        public void DeleteDebts(IEnumerable<long> ids) =>
            InTransaction(() => { foreach (var id in ids) Exec("DELETE FROM debts WHERE id=@p0", id); });

        // ---------- 存錢目標 ----------
        public List<GoalRow> Goals() => Query(
            "SELECT g.id, g.name, g.target, COALESCE((SELECT SUM(amount) FROM goal_deposits WHERE goal_id = g.id), 0), " +
            "g.deadline, g.note, (SELECT COUNT(*) FROM goal_deposits WHERE goal_id = g.id), g.repeat, w.id, w.name " +
            "FROM goals g LEFT JOIN wallets w ON w.id = g.wallet_id ORDER BY g.id",
            r => new GoalRow(r.GetInt64(0), Str(r, 1), Num(r, 2), Num(r, 3), Str(r, 4), Str(r, 5), r.GetInt32(6), Str(r, 7),
                             r.IsDBNull(8) ? null : r.GetInt64(8), Str(r, 9)));

        public long AddGoal(string name, double target, string deadline, string note, string repeat = "", long? walletId = null)
        {
            Exec("INSERT INTO goals(name,target,deadline,note,created,repeat,wallet_id) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",
                 name, target, deadline, note, DateTime.Today.ToString("yyyy-MM-dd"), repeat, walletId);
            return (long)Scalar("SELECT last_insert_rowid()")!;
        }

        /// <summary>
        /// 指定錢包的每年繳費:在該錢包記一筆支出、從目標已存金額扣掉 withdraw(只動這個目標),
        /// 並把繳費日延後一年。
        /// </summary>
        public void PayYearlyFromWallet(long id, long walletId, string date, string category, double amount,
                                        string recordNote, double withdraw, string depositNote, string nextDeadline) => InTransaction(() =>
        {
            AddRecord(date, Expense, category, amount, recordNote, walletId);
            if (withdraw > 0) AddDeposit(id, date, -withdraw, depositNote);
            Exec("UPDATE goals SET deadline=@p0 WHERE id=@p1", nextDeadline, id);
        });

        /// <summary>每年繳費:從已存金額扣掉繳費金額(扣到 0 為止),並把繳費日延後一年。</summary>
        public void PayYearly(long id, string date, double withdraw, string note, string nextDeadline) => InTransaction(() =>
        {
            if (withdraw > 0) AddDeposit(id, date, -withdraw, note);
            Exec("UPDATE goals SET deadline=@p0 WHERE id=@p1", nextDeadline, id);
        });

        public void UpdateGoal(long id, string name, double target, string deadline, string note, long? walletId) =>
            Exec("UPDATE goals SET name=@p0, target=@p1, deadline=@p2, note=@p3, wallet_id=@p4 WHERE id=@p5",
                 name, target, deadline, note, walletId, id);

        public void DeleteGoal(long id) => InTransaction(() =>
        {
            Exec("DELETE FROM goal_deposits WHERE goal_id=@p0", id);
            Exec("DELETE FROM goals WHERE id=@p0", id);
        });

        public List<DepositRow> Deposits(long goalId) => Query(
            "SELECT id, date, amount, note FROM goal_deposits WHERE goal_id=@p0 ORDER BY date DESC, id DESC",
            r => new DepositRow(r.GetInt64(0), Str(r, 1), Num(r, 2), Str(r, 3)), goalId);

        /// <param name="amount">正數為存入,負數為取出</param>
        public void AddDeposit(long goalId, string date, double amount, string note) =>
            Exec("INSERT INTO goal_deposits(goal_id,date,amount,note) VALUES(@p0,@p1,@p2,@p3)", goalId, date, amount, note);

        /// <param name="amount">正數為存入,負數為取出</param>
        public void UpdateDeposit(long id, string date, double amount, string note) =>
            Exec("UPDATE goal_deposits SET date=@p0, amount=@p1, note=@p2 WHERE id=@p3", date, amount, note, id);

        public void DeleteDeposit(long id) => Exec("DELETE FROM goal_deposits WHERE id=@p0", id);

        // ---------- 匯出 / 匯入 ----------
        public static readonly string[] Tables = ["wallets", "categories", "records", "transfers", "debts", "goals", "goal_deposits"];

        /// <summary>讀出整張表(含 rowid 順序),供完整備份使用。</summary>
        public List<Dictionary<string, object?>> Dump(string table)
        {
            if (!Tables.Contains(table)) throw new ArgumentException(table);
            return Query($"SELECT * FROM {table} ORDER BY rowid", r =>
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                return row;
            });
        }

        /// <summary>以備份內容取代所有資料。只接受已知的表與欄位。</summary>
        public void ReplaceAll(Dictionary<string, List<Dictionary<string, object?>>> data) => InTransaction(() =>
        {
            foreach (var table in Tables)
            {
                Exec($"DELETE FROM {table}");
                if (!data.TryGetValue(table, out var rows)) continue;
                var known = Columns(table);
                foreach (var row in rows)
                {
                    var cols = row.Keys.Where(known.Contains).ToList();
                    if (cols.Count == 0) continue;
                    var ps = string.Join(",", cols.Select((_, i) => $"@p{i}"));
                    Exec($"INSERT INTO {table}({string.Join(",", cols)}) VALUES({ps})", cols.Select(c => row[c]).ToArray());
                }
            }
            EnsureDefaults();
        });

        public long Count(string table)
        {
            if (!Tables.Contains(table)) throw new ArgumentException(table);
            return Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {table}"));
        }

        /// <summary>刪除全部資料,只留預設的「現金」錢包與預設分類。</summary>
        public void ClearAll() => ReplaceAll([]);

        /// <summary>把目前資料庫完整複製一份到指定檔案。</summary>
        public void BackupTo(string path) => Exec($"VACUUM INTO '{path.Replace("'", "''")}'");

        /// <summary>追加收支記錄;不存在的錢包與分類會自動建立。</summary>
        public void ImportRecords(IEnumerable<(string Date, string Wallet, string Type, string Category, double Amount, string Note)> rows) =>
            InTransaction(() =>
            {
                var wallets = Query("SELECT name, id FROM wallets", r => (Name: Str(r, 0), Id: r.GetInt64(1)))
                    .ToDictionary(w => w.Name, w => w.Id);
                foreach (var r in rows)
                {
                    var name = r.Wallet == "" ? wallets.Keys.First() : r.Wallet;
                    if (!wallets.TryGetValue(name, out var wid))
                    {
                        Exec($"INSERT INTO wallets(name, initial, sort) VALUES(@p0, 0, {NextWalletSort})", name);
                        wallets[name] = wid = (long)Scalar("SELECT last_insert_rowid()")!;
                    }
                    if (r.Category != "") Exec("INSERT OR IGNORE INTO categories VALUES(@p0,@p1)", r.Type, r.Category);
                    AddRecord(r.Date, r.Type, r.Category, r.Amount, r.Note, wid);
                }
            });
    }
}
