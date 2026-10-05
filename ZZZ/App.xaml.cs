using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ZZZ
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 日期一律顯示為 yyyy-MM-dd(與資料庫格式一致)
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("zh-TW").Clone();
            culture.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;

            DispatcherUnhandledException += OnUnhandled;
            SetTheme(LoadTheme(), save: false);
            base.OnStartup(e);
        }

        // ---------- 深色 / 淺色 ----------
        public static bool IsDark { get; private set; }

        static string ThemeFile => Path.Combine(Database.DataDir, "theme.txt");

        /// <summary>讀取上次的選擇;沒選過就跟隨 Windows 的「應用程式模式」。</summary>
        static bool LoadTheme()
        {
            try
            {
                if (File.Exists(ThemeFile)) return File.ReadAllText(ThemeFile).Trim() == "dark";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }

        /// <summary>
        /// 換掉第一個合併字典(配色)。介面以 DynamicResource 引用顏色,所以已開啟的視窗會立刻換色;
        /// 程式碼裡用 FindResource 設定的顏色要由呼叫端重新整理。
        /// </summary>
        public static void SetTheme(bool dark, bool save = true)
        {
            IsDark = dark;
            var colors = new ResourceDictionary { Source = new Uri(dark ? "Colors.Dark.xaml" : "Colors.Light.xaml", UriKind.Relative) };
            Current.Resources.MergedDictionaries[0] = colors;
            if (!save) return;
            try
            {
                Directory.CreateDirectory(Database.DataDir);
                File.WriteAllText(ThemeFile, dark ? "dark" : "light");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DialogWindow.Error(null, "發生錯誤", e.Exception.Message);
            e.Handled = true;
        }
    }
}
