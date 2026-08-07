using System.Windows;
using System.Windows.Threading;

namespace SafeGitPublisher.Tests;

/// <summary>
/// GUI 冒烟测试宿主：进程内只允许创建 1 个 Application（WPF 每 AppDomain 单例），
/// 因此所有 GUI 测试必须在此同一 STA 线程、同一 App 实例上顺序执行：
/// 主窗口 + 关于/设置/详细报告/最终确认 四个对话框。
/// </summary>
public static class GuiSmokeHost
{
    [Test]
    public static void GuiSmoke_AllWindows_Sequential()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                // 主窗口冒烟会 Close 主窗口：OnMainWindowClose 会触发应用关闭，
                // 后续对话框无法 Show，故测试宿主改为显式 Shutdown。
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                GuiStartupSmoke.RunMainWindowSmoke(app);
                DialogSmokeTests.RunAllDialogs(app);
            }
            catch (Exception ex)
            {
                failure = ex.InnerException ?? ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(300)))
        {
            throw new Exception("GUI 冒烟测试超时（300 秒）");
        }
        if (failure != null) throw failure;
    }
}
