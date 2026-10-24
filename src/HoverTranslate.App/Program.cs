using HoverTranslate.Core.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HoverTranslate.App;

public static class Program
{
    internal static SingleInstance? Instance { get; private set; }

    /// <summary>Аргументы командной строки: --quit (закрыть работающий экземпляр), --debug (подробный лог).</summary>
    internal static string[] Args { get; private set; } = [];

    [STAThread]
    private static int Main(string[] args)
    {
        Args = args;
        using var instance = new SingleInstance();
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            if (!instance.IsFirst) instance.SignalQuit();
            return 0;
        }
        if (!instance.IsFirst)
        {
            instance.SignalActivate(); // работающий экземпляр покажет уведомление
            return 0;
        }
        Instance = instance;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Сообщение до запуска UI или при фатальной ошибке.</summary>
    internal static void Fatal(string text) => TrayIcon.ShowMessage(text, "HoverTranslate", error: true);
}
