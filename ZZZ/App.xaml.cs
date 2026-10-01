using System.Globalization;
using System.Windows;
using System.Windows.Threading;

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
            base.OnStartup(e);
        }

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DialogWindow.Error(null, "發生錯誤", e.Exception.Message);
            e.Handled = true;
        }
    }
}
