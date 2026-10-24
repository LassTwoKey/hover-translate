namespace HoverTranslate.App;

/// <summary>
/// Единственный экземпляр приложения (именованный мьютекс в сессии пользователя) и сигналы
/// работающему экземпляру: «покажись» (повторный запуск) и «выйди» (--quit, нужен деинсталлятору).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\HoverTranslate.SingleInstance";
    private const string ActivateEventName = @"Local\HoverTranslate.Activate";
    private const string QuitEventName = @"Local\HoverTranslate.Quit";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _quit;
    private readonly CancellationTokenSource _stop = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirst = createdNew;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
    }

    /// <summary>Попросить работающий экземпляр показать себя.</summary>
    public void SignalActivate() => _activate.Set();

    /// <summary>Попросить работающий экземпляр завершиться.</summary>
    public void SignalQuit() => _quit.Set();

    /// <summary>Слушать сигналы (вызывать в первом экземпляре). Колбэки — в фоновом потоке.</summary>
    public void Listen(Action onActivate, Action onQuit)
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _activate, _quit, _stop.Token.WaitHandle };
            while (true)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index == 0) onActivate();
                else if (index == 1) onQuit();
                else return;
            }
        })
        {
            IsBackground = true,
            Name = "HoverTranslate.SingleInstance",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* мьютекс уже освобождён */ }
        }
        _mutex.Dispose();
        _activate.Dispose();
        _quit.Dispose();
        _stop.Dispose();
    }
}
