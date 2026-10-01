using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace ZZZ
{
    /// <summary>完整備份(JSON)與收支記錄(CSV)的匯出/匯入。</summary>
    public static class DataTransfer
    {
        const string AppId = "Ledger";
        const int FormatVersion = 1;
        public static readonly string[] CsvHeader = ["日期", "錢包", "類型", "分類", "金額", "備註"];

        public static string BackupDir => Path.Combine(Database.DataDir, "Backups");

        // ================= 完整備份 =================
        public static void ExportBackup(Database db, string path)
        {
            var doc = new Dictionary<string, object>
            {
                ["app"] = AppId,
                ["format"] = FormatVersion,
                ["version"] = Updater.CurrentVersion,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["tables"] = Database.Tables.ToDictionary(t => t, db.Dump),
            };
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), // 中文不轉成 \uXXXX
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, opts), new UTF8Encoding(false));
        }

        public record BackupSummary(string ExportedAt, int Wallets, int Records, int Debts, int Goals);

        /// <summary>讀取並檢查備份檔;格式不符時丟出 InvalidDataException。</summary>
        public static (Dictionary<string, List<Dictionary<string, object?>>> Data, BackupSummary Summary) ReadBackup(string path)
        {
            JsonDocument json;
            try { json = JsonDocument.Parse(File.ReadAllText(path)); }
            catch (JsonException) { throw new InvalidDataException("檔案不是有效的 JSON 格式。"); }
            using (json)
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("app", out var app) || app.GetString() != AppId ||
                    !root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("這不是簡易記帳的備份檔。");
                if (root.TryGetProperty("format", out var f) && f.TryGetInt32(out var fv) && fv > FormatVersion)
                    throw new InvalidDataException("這個備份檔來自較新的版本,請先更新軟體。");

                var data = new Dictionary<string, List<Dictionary<string, object?>>>();
                foreach (var table in Database.Tables)
                {
                    if (!tables.TryGetProperty(table, out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                    data[table] = rows.EnumerateArray()
                        .Where(r => r.ValueKind == JsonValueKind.Object)
                        .Select(r => r.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value)))
                        .ToList();
                }
                int Count(string t) => data.TryGetValue(t, out var l) ? l.Count : 0;
                var at = root.TryGetProperty("exportedAt", out var e) ? e.GetString() ?? "" : "";
                return (data, new BackupSummary(at, Count("wallets"), Count("records"), Count("debts"), Count("goals")));
            }
        }

        static object? ToValue(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            _ => v.GetRawText(),
        };

        /// <summary>匯入前自動備份目前資料,回傳備份檔路徑。</summary>
        public static string AutoBackup(Database db)
        {
            Directory.CreateDirectory(BackupDir);
            var path = Path.Combine(BackupDir, $"ledger-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            db.BackupTo(path);
            return path;
        }

        // ================= CSV =================
        static string Csv(string s) =>
            s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        public static void ExportCsv(IEnumerable<RecordRow> rows, string path)
        {
            var sb = new StringBuilder(string.Join(",", CsvHeader) + "\r\n");
            foreach (var r in rows)
                sb.Append(string.Join(",", Csv(r.Date), Csv(r.Wallet), Csv(r.Type), Csv(r.Category),
                    r.Amount.ToString(CultureInfo.InvariantCulture), Csv(r.Note))).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // 含 BOM,Excel 開啟才不會亂碼
        }

        public record CsvResult(
            List<(string Date, string Wallet, string Type, string Category, double Amount, string Note)> Rows,
            List<string> Errors);

        /// <summary>解析 CSV;欄位順序依標題列判斷,無法辨識的列會列在 Errors。</summary>
        public static CsvResult ReadCsv(string path)
        {
            var lines = ParseCsv(ReadText(path));
            if (lines.Count == 0) throw new InvalidDataException("檔案是空的。");
            var header = lines[0].Select(h => h.Trim()).ToList();
            var idx = CsvHeader.Select(h => header.IndexOf(h)).ToArray();
            if (idx[0] < 0 || idx[2] < 0 || idx[4] < 0)
                throw new InvalidDataException("找不到必要的欄位(日期、類型、金額)。\n請使用本程式匯出的 CSV 格式:" +
                                               string.Join(",", CsvHeader));

            var rows = new CsvResult([], []);
            for (int n = 1; n < lines.Count; n++)
            {
                var cells = lines[n];
                if (cells.All(c => c.Trim() == "")) continue;
                string Get(int i) => idx[i] >= 0 && idx[i] < cells.Count ? cells[idx[i]].Trim() : "";

                var dateOk = DateTime.TryParse(Get(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                             DateTime.TryParse(Get(0), out date);
                var type = Get(2);
                var amtOk = DialogWindow.TryParseAmount(Get(4), out var amt);
                if (!dateOk) rows.Errors.Add($"第 {n + 1} 行:日期「{Get(0)}」無法辨識");
                else if (type != Database.Income && type != Database.Expense) rows.Errors.Add($"第 {n + 1} 行:類型需為「收入」或「支出」");
                else if (!amtOk || amt == 0) rows.Errors.Add($"第 {n + 1} 行:金額「{Get(4)}」無效");
                else rows.Rows.Add((date.ToString("yyyy-MM-dd"), Get(1), type, Get(3), Math.Abs(amt), Get(5)));
            }
            return rows;
        }

        /// <summary>優先以 UTF-8 讀取;不是 UTF-8 時改用 Big5(Excel 另存的 CSV 常見)。</summary>
        static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(950).GetString(bytes);
            }
        }

        static List<List<string>> ParseCsv(string text)
        {
            var result = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    result.Add(row); row = [];
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); result.Add(row); }
            return result;
        }
    }
}
