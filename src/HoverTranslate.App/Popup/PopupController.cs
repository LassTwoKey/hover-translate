using HoverTranslate.Core;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.Translation;
using HoverTranslate.Core.Translation.Cloud;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace HoverTranslate.App.Popup;

/// <summary>
/// Когда показывать и закрывать карточку. Закрытие: Esc (LL-хук клавиатуры, только пока карточка открыта),
/// щелчок вне карточки (LL-хук мыши), повторное нажатие горячей клавиши на том же слове, кнопка ✕.
/// Закреплённая карточка не закрывается ни по Esc, ни по щелчку вне — хуки на это время снимаются.
/// </summary>
internal sealed partial class PopupController : IDisposable
{
    /// <summary>Сообщение «слово не найдено» закрывается само.</summary>
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(3);
    /// <summary>Допуск вокруг слова для «повторного нажатия на том же слове» (логические px).</summary>
    private const double SameWordSlack = 6;

    private readonly LookupCoordinator _coordinator;
    private readonly MessageWindow _messageWindow;
    private readonly ISpeechService _speech;
    private readonly IFavoritesStore _favorites;
    private readonly IHistoryStore _history;
    private readonly ISettingsStore _settings;
    private readonly AppSettings _appSettings;
    private readonly ITranslator _translator;
    private readonly ICloudTranslator _cloud;
    private readonly DispatcherQueue _ui;
    private readonly ILogger<PopupController> _log;
    private readonly InputHooks _hooks;
    private readonly ArrowWindow _arrow;
    private readonly DispatcherQueueTimer _messageTimer;
    private readonly DispatcherQueueTimer _autoCloseTimer;

    /// <summary>
    /// Слово/предложение часто находится через UIA за доли секунды — загрузку показываем, только если ждать дольше,
    /// чтобы карточка не мигала. OCR области всегда медленный — там загрузка сразу.
    /// </summary>
    private static readonly TimeSpan LookupLoadingDelay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueueTimer _loadingTimer;
    private PopupWindow? _window;
    private CancellationTokenSource? _translation;
    private volatile bool _loading;                 // на экране (или вот-вот) загрузка, результата ещё нет
    private int _hideEpoch;                         // растёт при каждом Hide
    private ScreenRect _loadingAnchor;

    // Снимок для потоков хуков/горячей клавиши (читается вне UI-потока).
    private volatile bool _visible;
    private ScreenRect _card, _arrowRect, _word;

    public PopupController(LookupCoordinator coordinator, MessageWindow messageWindow, ISpeechService speech,
        IFavoritesStore favorites, IHistoryStore history, ISettingsStore settings, ITranslator translator, ICloudTranslator cloud,
        DispatcherQueue ui, ILogger<PopupController> log)
    {
        _coordinator = coordinator;
        _messageWindow = messageWindow;
        _speech = speech;
        _favorites = favorites;
        _history = history;
        _settings = settings;
        _appSettings = new AppSettings(settings);
        _translator = translator;
        _cloud = cloud;
        _ui = ui;
        _log = log;
        _hooks = new InputHooks(messageWindow);
        _arrow = new ArrowWindow(messageWindow);
        _messageTimer = ui.CreateTimer();
        _messageTimer.IsRepeating = false;
        _messageTimer.Interval = MessageLifetime;
        _messageTimer.Tick += (_, _) => Hide();
        _autoCloseTimer = ui.CreateTimer();
        _autoCloseTimer.IsRepeating = false;
        _autoCloseTimer.Tick += (_, _) => Hide();
        _loadingTimer = ui.CreateTimer();
        _loadingTimer.IsRepeating = false;
        _loadingTimer.Interval = LookupLoadingDelay;
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            if (_loading) ShowLoading(_loadingAnchor, "Ищу текст…", area: false);
        };
    }

    public void Start()
    {
        _settings.Changed += OnSettingChanged;
        _coordinator.Completed += OnLookupCompleted;
        _coordinator.AreaCompleted += OnAreaCompleted;
        _coordinator.AreaSelectionStarting += OnAreaSelectionStarting;
        _coordinator.LookupStarted += OnLookupStarted;
        _coordinator.AreaRecognizing += OnAreaRecognizing;
        _coordinator.PressInterceptor = OnHotkeyPressed;
        _hooks.EscapePressed += (_, _) => _ui.TryEnqueue(Hide);
        _hooks.MouseDown += (_, p) =>
        {
            if (_visible && !Contains(_card, p, 0) && !Contains(_arrowRect, p, 0))
                _ui.TryEnqueue(Hide);
        };
        _hooks.HandlerError += (_, ex) => LogHookError(ex);
        _hooks.HookError += (_, code) => LogHookInstallFailed(code);
    }

    /// <summary>Настройка карточки изменилась (из любого потока): внешний вид применяется к открытой карточке сразу.</summary>
    private void OnSettingChanged(object? sender, string key)
    {
        if (key is SettingKeys.PopupFontPercent or SettingKeys.PopupOpacityPercent or SettingKeys.PopupTheme
            or SettingKeys.PopupExcludeFromCapture)
            _ui.TryEnqueue(() => _window?.ApplyAppearance(PopupAppearance.From(_appSettings)));
        else if (key == SettingKeys.PopupAutoCloseSeconds)
            _ui.TryEnqueue(RestartAutoClose);
    }

    /// <summary>
    /// Автозакрытие: отсчёт идёт, пока карточка открыта, не закреплена и над ней нет указателя;
    /// стоит уйти указателю — отсчёт начинается заново.
    /// </summary>
    private void RestartAutoClose()
    {
        _autoCloseTimer.Stop();
        int seconds = _appSettings.PopupAutoCloseSeconds;
        if (seconds <= 0 || _window is not { IsShown: true, IsPinned: false, IsPointerOver: false }) return;
        _autoCloseTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoCloseTimer.Start();
    }

    /// <summary>Повторное нажатие: на том же слове или над карточкой — закрыть, иначе — искать новое слово.</summary>
    private bool OnHotkeyPressed(ScreenPoint p)
    {
        if (!_visible) return false;
        double slack = SameWordSlack * Core.Capture.ScreenGeometry.MonitorFromPoint(p).Scale;
        if (Contains(_word, p, slack) || Contains(_card, p, 0))
        {
            _ui.TryEnqueue(Hide);
            return true;
        }
        return false;
    }

    private void OnLookupCompleted(object? sender, LookupOutcome outcome) => Show(outcome);

    /// <summary>Нажата клавиша на слове: якорь — курсор; карточка с загрузкой появится, если поиск затянется.</summary>
    private void OnLookupStarted(object? sender, ScreenPoint cursor)
    {
        _loading = true;
        _ui.TryEnqueue(() =>
        {
            if (!_loading) return; // результат или закрытие успели раньше
            _loadingAnchor = new ScreenRect(cursor.X - 1, cursor.Y - 1, 2, 2);
            _loadingTimer.Stop();
            _loadingTimer.Start();
        });
    }

    /// <summary>Область выбрана: карточка с загрузкой у прямоугольника сразу, пока идёт OCR.</summary>
    private void OnAreaRecognizing(object? sender, ScreenRect area)
    {
        _loading = true;
        _ui.TryEnqueue(() =>
        {
            if (_loading) ShowLoading(area, "Распознаю текст…", area: true);
        });
    }

    private void ShowLoading(ScreenRect anchor, string label, bool area) => _ = ShowLoadingAsync(anchor, label, area);

    private async Task ShowLoadingAsync(ScreenRect anchor, string label, bool area)
    {
        try
        {
            _window ??= CreateWindow();
            _messageTimer.Stop();
            _translation?.Cancel();
            int epoch = _hideEpoch;
            await _window.ShowAsync(new PopupContent.Loading(label, area), anchor);
            if (epoch != _hideEpoch)
            {
                Hide(); // закрыли, пока окно открывалось (первый показ долгий): не даём карточке воскреснуть
                return;
            }
            if (!_loading) return; // пришёл результат — он сам расставит якорь и хуки
            _word = anchor;
            UpdateSnapshot();
            UpdateHooks();
            _autoCloseTimer.Stop(); // загрузка не закрывается сама: перед ней нет результата, который можно читать
        }
        catch (Exception ex)
        {
            LogShowFailed(ex);
        }
    }

    /// <summary>Пришёл результат (или нажали закрыть): загрузка больше не нужна.</summary>
    private void EndLoading()
    {
        _loading = false;
        _loadingTimer.Stop();
    }

    /// <summary>Поток служебного окна: хуки карточки снимаются сразу (затемнению нужны свои), окно — в UI-потоке.</summary>
    private void OnAreaSelectionStarting(object? sender, EventArgs e)
    {
        _loading = false; // Hide ниже не должен отменять только что начатое выделение
        _visible = false;
        _hooks.Uninstall();
        _ui.TryEnqueue(Hide);
    }

    /// <summary>Показать карточку перевода предложения/области (флаг разработчика --demo-area; из любого потока).</summary>
    public void ShowArea(AreaOutcome outcome) => OnAreaCompleted(null, outcome);

    private void OnAreaCompleted(object? sender, AreaOutcome outcome) => _ui.TryEnqueue(async () =>
    {
        try
        {
            await ShowAreaAsync(outcome);
        }
        catch (Exception ex)
        {
            LogShowFailed(ex);
        }
    });

    /// <summary>Перевод выделенной области: карточка у прямоугольника, оригинал сразу, перевод — когда будет готов.</summary>
    private async Task ShowAreaAsync(AreaOutcome outcome)
    {
        EndLoading();
        _window ??= CreateWindow();
        _messageTimer.Stop();
        _translation?.Cancel();
        PopupContent content = outcome.Text is { } text
            ? new PopupContent.Area(text, outcome.Highlight, outcome.HighlightOffset)
            : new PopupContent.Message(outcome.Error ?? "Текст не найден",
                "Выделите область с английским текстом крупнее — OCR не читает очень мелкий шрифт.");
        await _window.ShowAsync(content, outcome.Area);
        _word = outcome.Area;
        UpdateSnapshot();
        UpdateHooks();
        RestartAutoClose();
        if (content is PopupContent.Message && !_window.IsPinned)
            _messageTimer.Start();
        else if (outcome.Text is { } en)
            await TranslateAsync(en, explainUnavailable: true, allowAutoCloud: true);
    }

    /// <summary>
    /// Показать карточку для результата (из любого потока). <paramref name="sample"/> — пример для окна настроек:
    /// закреплён (не закрывается по щелчку в настройках), в историю не пишется.
    /// </summary>
    public void Show(LookupOutcome outcome, bool sample = false) => _ui.TryEnqueue(async () =>
    {
        try
        {
            await ShowAsync(outcome, sample);
        }
        catch (Exception ex)
        {
            LogShowFailed(ex);
        }
    });

    private async Task ShowAsync(LookupOutcome outcome, bool sample)
    {
        EndLoading();
        _window ??= CreateWindow();
        _messageTimer.Stop();
        _translation?.Cancel();

        var cursor = outcome.Capture.Cursor;
        ScreenRect anchor;
        PopupContent content;
        if (outcome.Result is { } result && outcome.Capture.Hit is { } hit)
        {
            anchor = hit.WordBounds;
            content = new PopupContent.Result(result);
            if (!sample)
                _ = Task.Run(() => _history.Record(result)); // запись на диск — не в UI-потоке; ошибки HistoryStore пишет в лог
        }
        else
        {
            anchor = new ScreenRect(cursor.X - 1, cursor.Y - 1, 2, 2);
            content = new PopupContent.Message(outcome.Capture.Error ?? "Слово под курсором не найдено",
                "Наведите курсор точно на английское слово и нажмите горячую клавишу ещё раз.");
        }

        await _window.ShowAsync(content, anchor);
        _word = anchor;
        if (sample) _window.SetPinned(true);
        UpdateSnapshot();
        UpdateHooks();
        RestartAutoClose();

        if (content is PopupContent.Message && !_window.IsPinned)
            _messageTimer.Start();
        else if (outcome.Result is { Sentence: { } sentence })
            await TranslateAsync(sentence, explainUnavailable: false, allowAutoCloud: false);
    }

    // ---- перевод предложения в карточке: встроенная модель сразу, облако (Gemini) подменяет, когда придёт

    private string? _sourceText;                      // что переводится в открытой карточке
    private string? _localRu, _cloudRu, _cloudError;  // готовые переводы и причина, по которой облака нет
    private TranslationEngine _shown;                 // чей перевод на экране
    private bool _userChose;                          // пользователь сам переключил «Локально | Облако»
    private bool _localPending, _cloudBusy;

    /// <summary>
    /// Перевод в фоне; новое нажатие отменяет предыдущий. До загрузки модели у слова блок перевода скрыт,
    /// а у области (<paramref name="explainUnavailable"/>) — пояснение, почему перевода нет.
    /// Если облако включено и есть ключ, предложения и области (<paramref name="allowAutoCloud"/>) переводятся и им:
    /// встроенный перевод показывается сразу, облачный подменяет его, когда придёт; при ошибке остаётся встроенный
    /// с пояснением. В облако сам уходит только текст из нескольких слов — не одиночное слово.
    /// </summary>
    private async Task TranslateAsync(string text, bool explainUnavailable, bool allowAutoCloud)
    {
        _translation?.Cancel();
        var cts = _translation = new CancellationTokenSource();
        var window = _window!;
        _sourceText = text;
        _localRu = _cloudRu = _cloudError = null;
        _shown = TranslationEngine.Local;
        _userChose = _localPending = _cloudBusy = false;
        window.SetTranslatorNote(null);
        window.SetEngineSwitch(_cloud.HasKey, TranslationEngine.Local);

        bool autoCloud = allowAutoCloud && _cloud.IsActive && text.Contains(' ');
        if (autoCloud) _ = RunCloudAsync(text, cts);
        await TranslateLocalAsync(text, explainUnavailable, autoCloud, cts).ConfigureAwait(true);
    }

    private async Task TranslateLocalAsync(string text, bool explainUnavailable, bool cloudPending, CancellationTokenSource cts)
    {
        var window = _window!;
        if (!_translator.IsReady)
        {
            if (cloudPending)
            {
                if (_cloudRu is null) window.SetSentenceTranslation(null, loading: true); // ждём облако
                return;
            }
            string? why = !explainUnavailable ? null
                : _translator.State == TranslatorState.Loading
                    ? "Модель перевода ещё загружается — повторите через несколько секунд."
                    : @"Модель перевода не найдена (data\models\opus-mt-en-ru рядом с программой).";
            window.SetSentenceTranslation(why, loading: false);
            return;
        }
        _localPending = true;
        if (_shown == TranslationEngine.Local && _cloudRu is null) window.SetSentenceTranslation(null, loading: true);
        try
        {
            string ru = await _translator.TranslateAsync(text, cts.Token);
            if (!IsCurrent(cts)) return;
            _localRu = ru;
            _localPending = false;
            if (_shown == TranslationEngine.Local) ShowCurrent();
            else UpdateNote();
        }
        catch (OperationCanceledException)
        {
            // новое нажатие — перевод больше не нужен
        }
        catch (Exception ex)
        {
            LogTranslateFailed(ex);
            if (!IsCurrent(cts)) return;
            _localPending = false;
            if (_shown == TranslationEngine.Local) ShowCurrent();
        }
    }

    private bool IsCurrent(CancellationTokenSource cts) => ReferenceEquals(_translation, cts) && !cts.IsCancellationRequested;

    /// <summary>Запрос в облако; ошибки наружу не выходят: карточка получает подсказку и остаётся со встроенным переводом.</summary>
    private async Task RunCloudAsync(string text, CancellationTokenSource cts)
    {
        _cloudBusy = true;
        _cloudError = null;
        if (_shown == TranslationEngine.Cloud) _window!.SetSentenceTranslation(null, loading: true);
        UpdateNote();
        try
        {
            string ru = await _cloud.TranslateAsync(text, cts.Token);
            if (!IsCurrent(cts)) return;
            _cloudRu = ru;
            _cloudBusy = false;
            if (!_userChose || _shown == TranslationEngine.Cloud) _shown = TranslationEngine.Cloud; // подмена, если не выбрано вручную
            ShowCurrent();
        }
        catch (OperationCanceledException)
        {
            // карточка закрыта или открыта другая
        }
        catch (Exception ex)
        {
            if (ex is not CloudException) LogTranslateFailed(ex);
            if (!IsCurrent(cts)) return;
            _cloudBusy = false;
            _cloudError = ex is CloudException ce ? ce.UserMessage : "непредвиденная ошибка";
            if (_shown == TranslationEngine.Cloud) _shown = TranslationEngine.Local; // вернулись к встроенному
            ShowCurrent();
        }
    }

    /// <summary>Показывает перевод выбранного источника (или заглушку/пояснение, если его ещё нет) и обновляет переключатель и подсказку.</summary>
    private void ShowCurrent()
    {
        var window = _window!;
        string? text = _shown == TranslationEngine.Cloud ? _cloudRu : _localRu;
        bool waiting = _shown == TranslationEngine.Cloud ? _cloudBusy : _localPending || (_cloudBusy && !_translator.IsReady);
        if (text is not null)
            window.SetSentenceTranslation(text, loading: false);
        else if (waiting)
            window.SetSentenceTranslation(null, loading: true);
        else if (_cloudError is not null && !_translator.IsReady)
            window.SetSentenceTranslation($"Перевод недоступен: облако — {_cloudError}.", loading: false);
        else
            window.SetSentenceTranslation(null, loading: false);
        window.SetEngineSwitch(_cloud.HasKey, _shown);
        UpdateNote();
    }

    private void UpdateNote()
    {
        string? note = null;
        if (_shown == TranslationEngine.Cloud)
            note = _cloudBusy ? "Gemini переводит…" : "Перевод Gemini";
        else if (_cloudBusy)
            note = "Облачный перевод Gemini идёт — показан встроенный.";
        else if (_cloudError is not null && _localRu is not null)
            note = $"Облако недоступно: {_cloudError}. Показан встроенный перевод.";
        _window?.SetTranslatorNote(note);
    }

    /// <summary>Кнопки «Локально | Облако»: явный выбор пользователя; облако по кнопке работает, даже если оно выключено в настройках.</summary>
    private void OnEngineSelected(TranslationEngine engine)
    {
        if (_window is null || _sourceText is null || _translation is not { IsCancellationRequested: false } cts) return;
        _userChose = true;
        _shown = engine;
        if (engine == TranslationEngine.Local)
        {
            if (_localRu is null && !_localPending && _translator.IsReady)
                _ = TranslateLocalAsync(_sourceText, explainUnavailable: false, cloudPending: false, cts);
            ShowCurrent();
            return;
        }
        if (_cloudRu is null && !_cloudBusy)
        {
            if (!_cloud.HasKey)
            {
                _shown = TranslationEngine.Local;
                ShowCurrent();
                _window.SetTranslatorNote("Ключ Gemini не задан: добавьте его в настройках (значок в трее).");
                return;
            }
            _ = RunCloudAsync(_sourceText, cts);
            return;
        }
        ShowCurrent();
    }

    private PopupWindow CreateWindow()
    {
        var window = new PopupWindow(_arrow, _speech, _favorites, PopupAppearance.From(_appSettings));
        window.CloseRequested += (_, _) => Hide();
        window.LayoutChanged += (_, _) => UpdateSnapshot(); // хук мыши сверяет клики с актуальным прямоугольником
        window.EngineSelected += (_, engine) => OnEngineSelected(engine);
        window.PinnedChanged += (_, pinned) =>
        {
            if (pinned) _messageTimer.Stop();
            UpdateHooks();
            RestartAutoClose();
        };
        window.PointerOverChanged += (_, _) => RestartAutoClose();
        return window;
    }

    public void Hide()
    {
        if (_loading) _coordinator.CancelPending(); // закрыли карточку с загрузкой — результат уже не нужен
        EndLoading();
        _hideEpoch++;
        _messageTimer.Stop();
        _autoCloseTimer.Stop();
        _translation?.Cancel();
        _visible = false;
        _hooks.Uninstall();
        _window?.HidePopup();
    }

    private void UpdateSnapshot()
    {
        if (_window is null) return;
        _card = _window.CardRect;
        _arrowRect = _window.ArrowRect;
        _visible = _window.IsShown;
    }

    private void UpdateHooks()
    {
        if (_window is { IsShown: true, IsPinned: false })
            _hooks.Install(keyboard: true, mouse: true);
        else
            _hooks.Uninstall();
    }

    private static bool Contains(ScreenRect r, ScreenPoint p, double slack) =>
        !r.IsEmpty && p.X >= r.X - slack && p.X < r.Right + slack && p.Y >= r.Y - slack && p.Y < r.Bottom + slack;

    public void Dispose()
    {
        _settings.Changed -= OnSettingChanged;
        _autoCloseTimer.Stop();
        _coordinator.Completed -= OnLookupCompleted;
        _coordinator.AreaCompleted -= OnAreaCompleted;
        _coordinator.AreaSelectionStarting -= OnAreaSelectionStarting;
        _coordinator.LookupStarted -= OnLookupStarted;
        _coordinator.AreaRecognizing -= OnAreaRecognizing;
        _loadingTimer.Stop();
        _coordinator.PressInterceptor = null;
        _translation?.Cancel();
        _hooks.Dispose();
        _arrow.Dispose();
        _window?.Close();
    }

    [LoggerMessage(LogLevel.Error, "Не удалось показать карточку")]
    private partial void LogShowFailed(Exception ex);

    [LoggerMessage(LogLevel.Warning, "Не удалось перевести предложение")]
    private partial void LogTranslateFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "Ошибка в обработчике хука ввода")]
    private partial void LogHookError(Exception ex);

    [LoggerMessage(LogLevel.Warning, "Не удалось поставить хук ввода: ошибка {Code}")]
    private partial void LogHookInstallFailed(int code);
}
