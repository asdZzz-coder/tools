using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ZZZ
{
    /// <param name="PackageUrl">ClickOnce 安裝包(zip)的下載網址;Release 上沒有附安裝包時為 null。</param>
    public record UpdateInfo(string Version, string Notes, string Url, string? PackageUrl, long Size);

    /// <summary>
    /// 線上更新:程式以 ClickOnce 安裝,但不使用 ClickOnce 內建的更新。
    /// 由本類別向 GitHub Releases 查詢最新版,使用者同意後下載 ClickOnce 安裝包(zip),
    /// 放回當初安裝的資料夾後開啟 Ledger.application,由 ClickOnce 升級成新版。
    /// 只有「安裝版」才能自動更新;免安裝版或從 Visual Studio / dotnet run 執行時,改為開啟下載頁面。
    /// </summary>
    public static class Updater
    {
        // 發布版本的 GitHub 倉庫(需為 Public,未登入才查得到最新 Release)
        public const string GitHubRepo = "asdZzz-coder/tools";

        const string ManifestName = "Ledger.application";

        /// <summary>下載與解壓更新包的資料夾(啟動時由 CleanupTemp 清掉)。</summary>
        static readonly string DownloadFolder = Path.Combine(Path.GetTempPath(), "Ledger-Update");

        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ledger-updater"); // GitHub API 要求有 User-Agent
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static string CurrentVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

        /// <summary>
        /// 是否為 ClickOnce 安裝版。ClickOnce 啟動程式時會設定 ClickOnce_IsNetworkDeployed;
        /// 保險起見也檢查執行位置是否在 ClickOnce 的安裝快取(%LocalAppData%\Apps\2.0)。
        /// </summary>
        public static bool IsInstalled =>
            string.Equals(Environment.GetEnvironmentVariable("ClickOnce_IsNetworkDeployed"), "true", StringComparison.OrdinalIgnoreCase)
            || AppContext.BaseDirectory.Contains(@"\Apps\2.0\", StringComparison.OrdinalIgnoreCase);

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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var latest = root.GetProperty("tag_name").GetString() ?? "";
            if (!IsNewer(latest, CurrentVersion)) return null;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            string? packageUrl = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("Ledger-ClickOnce", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (!IsGitHubUrl(url)) continue;
                    packageUrl = url;
                    size = asset.GetProperty("size").GetInt64();
                    break;
                }
            return new UpdateInfo(latest, notes.Trim(), root.GetProperty("html_url").GetString() ?? "", packageUrl, size);
        }

        /// <summary>只信任 GitHub 的下載網址,避免 API 回應被竄改後下載到別處的檔案。</summary>
        static bool IsGitHubUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
            (u.Host == "github.com" || u.Host.EndsWith(".github.com") || u.Host.EndsWith(".githubusercontent.com"));

        /// <summary>下載新版安裝包、解壓並啟動安裝;呼叫端應在這之後結束程式。</summary>
        public static async Task DownloadAndLaunchAsync(UpdateInfo info, Action<int>? progress = null)
        {
            if (info.PackageUrl == null) throw new InvalidOperationException("此版本沒有附上安裝包。");

            // 先清掉先前(例如失敗或中斷的更新)遺留的檔案
            TryDeleteDirectory(DownloadFolder);
            Directory.CreateDirectory(DownloadFolder);
            var tag = info.Version.TrimStart('v', 'V');
            var zipPath = Path.Combine(DownloadFolder, $"Ledger-{tag}.zip");
            var extractDir = Path.Combine(DownloadFolder, tag);

            using (var resp = await Http.GetAsync(info.PackageUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? info.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                var buf = new byte[81920];
                long done = 0;
                int last = -1, n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != last) { last = pct; progress?.Invoke(pct); }
                }
            }

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));
            if (!File.Exists(Path.Combine(extractDir, ManifestName)))
                throw new FileNotFoundException("更新包內容不完整,找不到安裝程式。", ManifestName);

            // ClickOnce 會記住「從哪個資料夾安裝的」,從別的位置安裝同一個程式會被拒絕。
            // 所以把新版放回當初安裝的同一個資料夾,再從那裡執行,ClickOnce 就會當成一般升級。
            var installDir = FindInstallSourceFolder() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ledger-Setup");
            await Task.Run(() => ReplaceInstallSource(extractDir, installDir));

            var manifest = Path.Combine(installDir, ManifestName);
            Process.Start(new ProcessStartInfo(manifest) { UseShellExecute = true, WorkingDirectory = installDir });
        }

        /// <summary>啟動時清掉更新後不再需要的下載檔(%TEMP%\Ledger-Update)。刪除失敗就略過,下次再清。</summary>
        public static void CleanupTemp() => Task.Run(() => TryDeleteDirectory(DownloadFolder));

        /// <summary>
        /// 找出當初安裝時用的 Ledger.application 所在資料夾:
        /// 先看 ClickOnce 提供的環境變數,再看開始功能表捷徑(.appref-ms 內記錄了安裝來源)。
        /// </summary>
        static string? FindInstallSourceFolder()
        {
            foreach (var name in new[] { "ClickOnce_UpdateLocation", "ClickOnce_ActivationUri" })
            {
                var dir = FolderFromManifestUri(Environment.GetEnvironmentVariable(name));
                if (dir != null) return dir;
            }

            try
            {
                var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                foreach (var file in Directory.EnumerateFiles(programs, "*.appref-ms", SearchOption.AllDirectories))
                {
                    // 內容格式:file:///C:/.../Ledger.application#Ledger.application, Culture=...
                    var text = File.ReadAllText(file);
                    if (!text.Contains(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = FolderFromManifestUri(text.Split('#')[0].Trim().Trim('\0', '\uFEFF'));
                    if (dir != null) return dir;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>只接受本機(或網路芳鄰)路徑的 .application;網址或不存在的磁碟回傳 null。</summary>
        static string? FolderFromManifestUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            var dir = Path.GetDirectoryName(uri.LocalPath);
            if (dir == null || !Directory.Exists(Path.GetPathRoot(dir))) return null;
            return dir;
        }

        /// <summary>
        /// 用新版安裝包取代安裝來源資料夾的內容:只動 ClickOnce 自己的檔案
        /// (Ledger.application、安裝.cmd、Application Files\Ledger_*),其他檔案不碰。
        /// </summary>
        static void ReplaceInstallSource(string from, string to)
        {
            Directory.CreateDirectory(to);
            var oldVersions = Path.Combine(to, "Application Files");
            if (Directory.Exists(oldVersions))
                foreach (var d in Directory.EnumerateDirectories(oldVersions, "Ledger_*"))
                    TryDeleteDirectory(d);

            foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(to, Path.GetRelativePath(from, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
        }

        static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 被占用,下次再清 */ }
        }
    }
}
