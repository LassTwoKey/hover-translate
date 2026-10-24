using System.Collections.ObjectModel;
using System.Globalization;
using HoverTranslate.Core;
using HoverTranslate.Core.Models;
using Microsoft.UI.Xaml;

namespace HoverTranslate.App;

/// <summary>Строка отладочного окна.</summary>
public sealed record OutcomeRow(string Word, string Meta, string Sentence, string Note, Visibility NoteVisibility)
{
    public static OutcomeRow From(CaptureOutcome o)
    {
        string time = o.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string meta = $"{time} · {o.Hit?.Source.ToString().ToUpperInvariant() ?? "—"} · {o.Elapsed.TotalMilliseconds:0} мс · {o.SourceApp}";
        string note = string.Join(" · ", new[] { o.Error, o.UiaNote }.Where(s => !string.IsNullOrEmpty(s)));
        return new OutcomeRow(
            o.Hit?.Word ?? "не найдено",
            meta,
            o.Hit?.Sentence ?? "",
            note,
            note.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
    }
}

/// <summary>Отладочное окно: результаты нажатий горячей клавиши вместо всплывающей карточки.</summary>
public sealed partial class DebugWindow : Window
{
    public const int MaxRows = 200;

    private readonly ObservableCollection<OutcomeRow> _rows;

    public DebugWindow(IEnumerable<CaptureOutcome> recent)
    {
        InitializeComponent();
        _rows = new ObservableCollection<OutcomeRow>(recent.Select(OutcomeRow.From));
        ResultsList.ItemsSource = _rows;
        HintText.Text = $"Наведите курсор на английское слово в любой программе и нажмите горячую клавишу. " +
                        $"Лог: {AppPaths.LogsDir}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HoverTranslate.ico"));
        double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(960 * scale), (int)(620 * scale)));
    }

    public void Add(CaptureOutcome outcome)
    {
        _rows.Insert(0, OutcomeRow.From(outcome));
        if (_rows.Count > MaxRows) _rows.RemoveAt(_rows.Count - 1);
    }
}
