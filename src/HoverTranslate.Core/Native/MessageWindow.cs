using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>Обработчик сообщения окна. Вернуть true, если сообщение обработано.</summary>
public delegate bool WindowMessageHandler(uint message, nuint wParam, nint lParam, out nint result);

/// <summary>
/// Невидимое Win32-окно на собственном потоке с циклом сообщений.
/// На нём живут RegisterHotKey и иконка в трее — так Core не зависит от UI-фреймворка,
/// а горячая клавиша срабатывает, даже если UI-поток занят.
/// </summary>
/// <remarks>
/// Окно верхнего уровня (скрытое), а не message-only: message-only окна не получают
/// широковещательное TaskbarCreated и не могут стать foreground для меню трея.
/// </remarks>
public sealed class MessageWindow : IDisposable
{
    private const uint WM_INVOKE = WM_APP + 1;
    private const string ClassName = "HoverTranslate.MessageWindow";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly ConcurrentDictionary<nint, MessageWindow> Windows = new();
    private static readonly Lock ClassLock = new();
    private static bool _classRegistered;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Thread _thread;
    private readonly List<WindowMessageHandler> _handlers = [];
    private volatile bool _disposed;

    public nint Handle { get; private set; }
    public int ThreadId { get; private set; }

    private MessageWindow(string threadName)
    {
        using var ready = new ManualResetEventSlim();
        Exception? startError = null;
        _thread = new Thread(() =>
        {
            try
            {
                Handle = CreateHiddenWindow();
                ThreadId = Environment.CurrentManagedThreadId;
                Windows[Handle] = this;
            }
            catch (Exception ex)
            {
                startError = ex;
                return;
            }
            finally
            {
                ready.Set();
            }
            RunLoop();
        })
        {
            Name = threadName,
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (startError is not null)
            throw new InvalidOperationException("Не удалось создать служебное окно", startError);
    }

    public static MessageWindow Start(string threadName = "HoverTranslate.MessageLoop") => new(threadName);

    /// <summary>Добавляет обработчик сообщений. Вызывается на потоке окна.</summary>
    public void AddHandler(WindowMessageHandler handler) => Invoke(() => _handlers.Add(handler));

    public void RemoveHandler(WindowMessageHandler handler) => Invoke(() => _handlers.Remove(handler));

    public bool IsOnWindowThread => Environment.CurrentManagedThreadId == ThreadId;

    /// <summary>Выполняет действие на потоке окна и ждёт завершения.</summary>
    public void Invoke(Action action)
    {
        if (IsOnWindowThread)
        {
            action();
            return;
        }
        InvokeAsync(() => { action(); return true; }).GetAwaiter().GetResult();
    }

    public T Invoke<T>(Func<T> func) => IsOnWindowThread ? func() : InvokeAsync(func).GetAwaiter().GetResult();

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!PostMessage(Handle, WM_INVOKE, 0, 0))
            tcs.TrySetException(new InvalidOperationException($"PostMessage: {Marshal.GetLastPInvokeError()}"));
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _queue.Enqueue(() => DestroyWindow(Handle));
        PostMessage(Handle, WM_INVOKE, 0, 0);
        _disposed = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private static unsafe nint CreateHiddenWindow()
    {
        nint hInstance = GetModuleHandle(null);
        lock (ClassLock)
        {
            if (!_classRegistered)
            {
                fixed (char* name = ClassName)
                {
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        lpfnWndProc = &WndProc,
                        hInstance = hInstance,
                        lpszClassName = name,
                    };
                    if (RegisterClassEx(wc) == 0)
                        throw new InvalidOperationException($"RegisterClassEx: {Marshal.GetLastPInvokeError()}");
                }
                _classRegistered = true;
            }
        }
        nint hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, ClassName, "HoverTranslate", WS_POPUP,
            0, 0, 0, 0, 0, 0, hInstance, 0);
        if (hwnd == 0)
            throw new InvalidOperationException($"CreateWindowEx: {Marshal.GetLastPInvokeError()}");
        return hwnd;
    }

    private static void RunLoop()
    {
        while (GetMessage(out MSG msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window))
            return DefWindowProc(hwnd, msg, wParam, lParam);
        return window.HandleMessage(hwnd, msg, wParam, lParam);
    }

    private nint HandleMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_INVOKE:
                while (_queue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { UnhandledException?.Invoke(this, ex); }
                }
                return 0;
            case WM_DESTROY:
                Windows.TryRemove(hwnd, out _);
                PostQuitMessage(0);
                return 0;
        }

        foreach (var handler in _handlers.ToArray())
        {
            try
            {
                if (handler(msg, wParam, lParam, out nint result))
                    return result;
            }
            catch (Exception ex)
            {
                // Исключение не должно пересекать границу WndProc — иначе процесс упадёт.
                UnhandledException?.Invoke(this, ex);
                return 0;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Исключения из обработчиков (их нельзя пробрасывать через WndProc).</summary>
    public event EventHandler<Exception>? UnhandledException;
}
