namespace HoverTranslate.Core.Models;

/// <summary>Точка в физических пикселях экрана (процесс работает в Per-Monitor DPI v2).</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Прямоугольник в физических пикселях экрана.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>Расстояние от точки до прямоугольника (0, если точка внутри).</summary>
    public double DistanceTo(double x, double y)
    {
        double dx = Math.Max(Math.Max(X - x, 0), x - Right);
        double dy = Math.Max(Math.Max(Y - y, 0), y - Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public ScreenRect Union(ScreenRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        double x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new ScreenRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

/// <summary>Результат этапа «текст под курсором» (до словаря и перевода).</summary>
/// <param name="Word">Нормализованное слово.</param>
/// <param name="RawWord">Слово как распознано/получено, до нормализации.</param>
/// <param name="Sentence">Предложение-контекст или null.</param>
/// <param name="WordBounds">Прямоугольник слова на экране (для позиционирования popup).</param>
/// <param name="WordOffset">Смещение слова в <paramref name="Sentence"/> или -1 (для поиска фраз).</param>
public sealed record TextHit(
    string Word,
    string RawWord,
    string? Sentence,
    ScreenRect WordBounds,
    TextSourceKind Source,
    int WordOffset = -1);

/// <summary>Итог распознавания области (Alt+W); перевод делает карточка.</summary>
/// <param name="Area">Выделенный прямоугольник (физические px).</param>
/// <param name="Text">Текст области, абзацы через перевод строки; null — текста нет или ошибка.</param>
/// <param name="Highlight">Слово под курсором, которое карточка подсвечивает в оригинале (перевод предложения); null — без подсветки.</param>
/// <param name="HighlightOffset">Смещение <paramref name="Highlight"/> в <paramref name="Text"/> или -1, если неизвестно.</param>
public sealed record AreaOutcome(DateTimeOffset Timestamp, ScreenRect Area, string? Text, TimeSpan Elapsed, string? Error,
    string? Highlight = null, int HighlightOffset = -1);

/// <summary>Итог нажатия: захват текста (для отладки) и результат для карточки (null — слово не найдено).</summary>
public sealed record LookupOutcome(CaptureOutcome Capture, LookupResult? Result);

/// <summary>Полный итог обработки нажатия горячей клавиши — для лога и отладочного окна.</summary>
public sealed record CaptureOutcome(
    DateTimeOffset Timestamp,
    ScreenPoint Cursor,
    string SourceApp,
    TextHit? Hit,
    TimeSpan Elapsed,
    string? UiaNote,
    string? Error);
