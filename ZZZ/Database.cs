using System.IO;
using Microsoft.Data.Sqlite;

namespace ZZZ
{
    public record Wallet(long Id, string Name, double Balance);

    public record RecordRow(long Id, string Date, string Wallet, string Type,
                            string Category, double Amount, string Note)
    {
        public bool IsIncome => Type == Database.Income;
        public string AmountText => (IsIncome ? "+" : "−") + Amount.ToString("N0");
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
    public record GoalRow(long Id, string Name, double Target, double Saved, string Deadline, string Note, int Count,
                          string Repeat = "")
    {
        public bool Yearly => Repeat == Database.Yearly;
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
        /// <summary>卡片名稱下方那一行:每年繳費一律顯示繳費日,一次性目標有備註就顯示備註。</summary>
        public string SubText => Yearly ? (Note != "" ? $"{DeadlineText} · {Note}" : DeadlineText)
                                        : Note != "" ? Note : DeadlineText;

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
            EnsureDefaults();
        }

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
        public List<Wallet> Wallets() => Query(
            "SELECT w.id, w.name, w.initial + COALESCE(SUM(CASE r.type WHEN '收入' THEN r.amount ELSE -r.amount END), 0) " +
            "FROM wallets w LEFT JOIN records r ON r.wallet_id = w.id GROUP BY w.id ORDER BY w.sort, w.id",
            r => new Wallet(r.GetInt64(0), Str(r, 1), Num(r, 2)));

        /// <returns>新錢包 id;名稱重複時回傳 null</returns>
        public long? AddWallet(string name, double initial)
        {
            try
            {
                Exec($"INSERT INTO wallets(name, initial, sort) VALUES(@p0,@p1,{NextWalletSort})", name, initial);
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

        public int WalletCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM wallets"));

        public int WalletRecordCount(long id) =>
            Convert.ToInt32(Scalar("SELECT COUNT(*) FROM records WHERE wallet_id=@p0", id));

        public void DeleteWallet(long id) => InTransaction(() =>
        {
            Exec("DELETE FROM records WHERE wallet_id=@p0", id);
            Exec("DELETE FROM wallets WHERE id=@p0", id);
        });

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

        public void DeleteRecords(IEnumerable<long> ids) =>
            InTransaction(() => { foreach (var id in ids) Exec("DELETE FROM records WHERE id=@p0", id); });

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
            "SELECT g.id, g.name, g.target, COALESCE(SUM(d.amount), 0), g.deadline, g.note, COUNT(d.id), g.repeat " +
            "FROM goals g LEFT JOIN goal_deposits d ON d.goal_id = g.id GROUP BY g.id ORDER BY g.id",
            r => new GoalRow(r.GetInt64(0), Str(r, 1), Num(r, 2), Num(r, 3), Str(r, 4), Str(r, 5), r.GetInt32(6), Str(r, 7)));

        public long AddGoal(string name, double target, string deadline, string note, string repeat = "")
        {
            Exec("INSERT INTO goals(name,target,deadline,note,created,repeat) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",
                 name, target, deadline, note, DateTime.Today.ToString("yyyy-MM-dd"), repeat);
            return (long)Scalar("SELECT last_insert_rowid()")!;
        }

        /// <summary>每年繳費:從已存金額扣掉繳費金額(扣到 0 為止),並把繳費日延後一年。</summary>
        public void PayYearly(long id, string date, double withdraw, string note, string nextDeadline) => InTransaction(() =>
        {
            if (withdraw > 0) AddDeposit(id, date, -withdraw, note);
            Exec("UPDATE goals SET deadline=@p0 WHERE id=@p1", nextDeadline, id);
        });

        public void UpdateGoal(long id, string name, double target, string deadline, string note) =>
            Exec("UPDATE goals SET name=@p0, target=@p1, deadline=@p2, note=@p3 WHERE id=@p4",
                 name, target, deadline, note, id);

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
        public static readonly string[] Tables = ["wallets", "categories", "records", "debts", "goals", "goal_deposits"];

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
