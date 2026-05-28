using System.Threading;
using System.Windows;

namespace WebAPI
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _instanceMutex;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            // 单实例检查，防止重复启动导致端口冲突
            const string mutexName = "Global\\WebAPI_SingleInstance_Mutex";
            bool createdNew;
            _instanceMutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew)
            {
                System.Windows.MessageBox.Show("WebAPI 已经在运行中，不能重复启动。", "提示",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 初始化日志目录
            Common.LogManager.Init();
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
