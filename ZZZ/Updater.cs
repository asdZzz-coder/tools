using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ZZZ
{
    public record UpdateInfo(string Version, string Notes, string Url);

    public static class Updater
    {
        // TODO: 改成你自己的 GitHub 帳號/倉庫名稱(倉庫需為 Public)
        public const string GitHubRepo = "你的帳號/ledger";

        public static string CurrentVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

        static int[] Parse(string v) =>
            v.TrimStart('v', 'V').Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();

        static bool IsNewer(string latest, string current)
        {
            int[] a = Parse(latest), b = Parse(current);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        /// <summary>有新版回傳資訊;已是最新回傳 null;失敗(沒網路、倉庫不存在、尚無 Release)丟出例外。</summary>
        public static async Task<UpdateInfo?> CheckAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ledger-updater");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var latest = root.GetProperty("tag_name").GetString() ?? "";
            if (!IsNewer(latest, CurrentVersion)) return null;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(latest, notes.Trim(), root.GetProperty("html_url").GetString() ?? "");
        }
    }
}
