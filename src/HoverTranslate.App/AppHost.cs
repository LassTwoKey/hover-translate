using HoverTranslate.App.Popup;
using HoverTranslate.Core;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Dictionary;
using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Native;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.TextSource;
using HoverTranslate.Core.Translation;
using HoverTranslate.Core.Translation.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Serilog;
using Serilog.Events;

namespace HoverTranslate.App;

/// <summary>Сборка DI-контейнера и логирования.</summary>
internal static class AppHost
{
    public static IHost Build(string[] args, IAppShell shell)
    {
        bool debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true, // без appsettings/переменных окружения/консольного лога
        });

        Directory.CreateDirectory(AppPaths.LogsDir);
        builder.Services.AddSerilog((_, log) => log
            .MinimumLevel.Is(debug ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "hovertranslate-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        var services = builder.Services;
        services.AddSingleton(shell);
        services.AddSingleton(DispatcherQueue.GetForCurrentThread()); // Build вызывается в UI-потоке
        services.AddSingleton(_ => MessageWindow.Start());
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<AppSettings>();
        services.AddSingleton<Autostart>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<ForegroundWatcher>();
        services.AddSingleton<ExclusionService>();
        services.AddSingleton<GraphicsCaptureService>();
        services.AddSingleton<GdiScreenCapture>();
        services.AddSingleton<IUiaTextSource, UiaTextSource>();
        if (debug) services.AddSingleton<OcrDebugDump>(); // снимки и сырой текст OCR в %LocalAppData%\HoverTranslate\ocr-debug
        services.AddSingleton<IOcrTextSource, OcrTextSource>();
        services.AddSingleton<ITextLookupService, TextLookupService>();
        services.AddSingleton<IDictionaryService, DictionaryService>();
        services.AddSingleton<ISpeechService, SpeechService>();
        services.AddSingleton<IFavoritesStore, FavoritesStore>();
        services.AddSingleton<IHistoryStore, HistoryStore>();
        services.AddSingleton<ITranslationCache, SqliteTranslationCache>();
        services.AddSingleton(_ => new HttpClient { BaseAddress = GeminiClient.BaseAddress, Timeout = Timeout.InfiniteTimeSpan }); // таймаут — в GeminiClient
        services.AddSingleton<GeminiClient>();
        services.AddSingleton<CloudKeyStore>();
        services.AddSingleton<CloudUsage>(sp => new CloudUsage(sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<CloudTranslator>();
        services.AddSingleton<ICloudTranslator>(sp => sp.GetRequiredService<CloudTranslator>());
        services.AddSingleton<Translator>();
        services.AddSingleton<ITranslator>(sp => sp.GetRequiredService<Translator>());
        services.AddSingleton<AreaSelector>();
        services.AddSingleton<MouseButtonHotkeys>();
        services.AddSingleton<PopupController>();
        services.AddSingleton<LookupCoordinator>();
        services.AddHostedService<HoverTranslateService>();
        return builder.Build();
    }
}
