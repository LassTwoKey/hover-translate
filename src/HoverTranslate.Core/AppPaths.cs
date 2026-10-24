namespace HoverTranslate.Core;

/// <summary>Расположение файлов приложения.</summary>
public static class AppPaths
{
    /// <summary>%LocalAppData%\HoverTranslate — настройки, история, кэш, логи.</summary>
    public static string UserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoverTranslate");

    public static string LogsDir { get; } = Path.Combine(UserDataDir, "logs");

    /// <summary>Снимки и сырой текст OCR в режиме --debug (хранятся последние несколько десятков).</summary>
    public static string OcrDebugDir { get; } = Path.Combine(UserDataDir, "ocr-debug");

    /// <summary>Пользовательская база: настройки (позже — история, избранное, кэш переводов).</summary>
    public static string UserDbPath { get; } = Path.Combine(UserDataDir, "user.db");

    /// <summary>Кэш переводов предложений — отдельный файл, чтобы показывать его размер и очищать целиком.</summary>
    public static string CacheDbPath { get; } = Path.Combine(UserDataDir, "cache.db");

    /// <summary>Данные рядом с exe: dict.db и модель перевода.</summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Модель opus-mt-en-ru (ONNX int8) и её токенизатор.</summary>
    public static string ModelDir { get; } = Path.Combine(DataDir, "models", "opus-mt-en-ru");
}
