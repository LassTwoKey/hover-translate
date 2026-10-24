using System.Text;
using System.Text.RegularExpressions;
using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.TextSource;

/// <summary>Слово из OCR: текст как распознан и прямоугольник в координатах экрана.</summary>
public sealed record OcrWordBox(string Text, ScreenRect Bounds);

/// <summary>Строка из OCR.</summary>
public sealed record OcrLineBox(IReadOnlyList<OcrWordBox> Words)
{
    public ScreenRect Bounds { get; } = Words.Aggregate(default(ScreenRect), (r, w) => r.Union(w.Bounds));

    /// <summary>Высота строки по медиане слов — устойчивее к выносным элементам и пунктуации.</summary>
    public double Height { get; } = Median(Words.Select(w => w.Bounds.Height));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}

/// <summary>Позиция слова: индекс строки и слова в ней.</summary>
public readonly record struct WordPosition(int Line, int Word);

/// <summary>Контекст слова: склеенный текст абзаца и смещение слова в нём.</summary>
public sealed record OcrContext(string Text, int WordOffset, int WordLength);

/// <summary>Хит-тест слова под курсором и сборка контекста по результату OCR. Чистая логика без Win32.</summary>
public static partial class OcrLayout
{
    // «Fenwick,this», «wait!Go»: знак препинания между буквами без пробела — OCR склеил два слова.
    [GeneratedRegex(@"^(?<a>[A-Za-z][A-Za-z'’\-]*)(?<p>[,;:!?])(?<b>[A-Za-z].*)$")]
    private static partial Regex GluedAfterPunctuation();

    // «it.l guess», «home.Then»: точка после слова из строчных, дальше заглавная или одиночная «l»/«I» (так OCR читает «I»).
    [GeneratedRegex(@"^(?<a>[a-z]{2,})(?<p>\.)(?<b>[A-Z].*|[lI])$")]
    private static partial Regex GluedAfterPeriod();

    /// <summary>
    /// Делит слова, которые OCR склеил без пробела после знака препинания («Fenwick,this» -> «Fenwick,» + «this»,
    /// «it.l» -> «it.» + «I»). Рамка делится пропорционально числу символов. Остальные слова не меняются.
    /// </summary>
    public static IReadOnlyList<OcrWordBox> SplitGluedWords(IReadOnlyList<OcrWordBox> words)
    {
        List<OcrWordBox>? result = null;
        for (int i = 0; i < words.Count; i++)
        {
            var parts = SplitGlued(words[i]);
            if (parts is null)
            {
                result?.Add(words[i]);
                continue;
            }
            result ??= [.. words.Take(i)];
            result.AddRange(parts);
        }
        return result ?? words;
    }

    private static List<OcrWordBox>? SplitGlued(OcrWordBox word)
    {
        var match = GluedAfterPunctuation().Match(word.Text);
        if (!match.Success) match = GluedAfterPeriod().Match(word.Text);
        if (!match.Success) return null;

        string first = match.Groups["a"].Value + match.Groups["p"].Value;
        string rest = match.Groups["b"].Value;
        if (rest == "l") rest = "I";
        var b = word.Bounds;
        double firstWidth = b.Width * first.Length / word.Text.Length;
        var parts = new List<OcrWordBox>
        {
            new(first, new ScreenRect(b.X, b.Y, firstWidth, b.Height)),
        };
        var tail = new OcrWordBox(rest, new ScreenRect(b.X + firstWidth, b.Y, b.Width - firstWidth, b.Height));
        parts.AddRange(SplitGlued(tail) ?? [tail]);
        return parts;
    }

    /// <summary>Сколько соседних строк сверху и снизу добавлять в контекст.</summary>
    public const int MaxNeighborLines = 3;

    /// <summary>
    /// Слово, чей прямоугольник содержит точку; иначе ближайшее в пределах <paramref name="tolerance"/>.
    /// Слова, от которых после нормализации ничего не остаётся («—», «•»), не выбираются.
    /// </summary>
    public static WordPosition? FindWord(IReadOnlyList<OcrLineBox> lines, double x, double y, double tolerance)
    {
        WordPosition? best = null;
        double bestDistance = double.MaxValue;
        for (int li = 0; li < lines.Count; li++)
        {
            var words = lines[li].Words;
            for (int wi = 0; wi < words.Count; wi++)
            {
                if (WordNormalizer.Normalize(words[wi].Text).Length == 0) continue;
                double d = words[wi].Bounds.DistanceTo(x, y);
                if (d < bestDistance)
                {
                    best = new WordPosition(li, wi);
                    bestDistance = d;
                }
            }
        }
        return bestDistance <= tolerance ? best : null;
    }

    /// <summary>
    /// Строка слова плюс до <see cref="MaxNeighborLines"/> соседних строк того же абзаца сверху и снизу.
    /// Соседняя строка — ближайшая по вертикали, перекрывается по горизонтали, с похожей высотой шрифта
    /// и небольшим межстрочным интервалом (так отсекаются соседние колонки, заголовки и подписи).
    /// </summary>
    public static OcrContext BuildContext(IReadOnlyList<OcrLineBox> lines, WordPosition position)
    {
        var block = ContextBlock(lines, position);
        return BuildText(lines, block, position);
    }

    /// <summary>Индексы строк абзаца слова (сверху вниз), включая строку самого слова.</summary>
    public static List<int> ContextBlock(IReadOnlyList<OcrLineBox> lines, WordPosition position)
    {
        var block = new List<int> { position.Line };
        int current = position.Line;
        for (int i = 0; i < MaxNeighborLines; i++)
        {
            int? above = FindNeighbor(lines, current, up: true);
            if (above is null || block.Contains(above.Value) || IsSpeakerLabel(lines[above.Value])) break;
            block.Insert(0, above.Value);
            current = above.Value;
        }
        current = position.Line;
        for (int i = 0; i < MaxNeighborLines; i++)
        {
            int? below = FindNeighbor(lines, current, up: false);
            if (below is null || block.Contains(below.Value) || IsSpeakerLabel(lines[below.Value])) break;
            block.Add(below.Value);
            current = below.Value;
        }
        return block;
    }

    private static OcrContext BuildText(IReadOnlyList<OcrLineBox> lines, List<int> block, WordPosition position)
    {
        var sb = new StringBuilder();
        int wordOffset = 0, wordLength = 0;
        foreach (int li in block)
        {
            var words = lines[li].Words;
            if (sb.Length > 0)
            {
                // Перенос по слогам: «inter-» + «national» -> «international».
                bool hyphenated = sb[^1] == '-' && words.Count > 0 && words[0].Text.Length > 0 && char.IsLower(words[0].Text[0]);
                if (hyphenated) sb.Length--;
                else sb.Append(' ');
            }
            int keep = KeptWordCount(words);
            if (li == position.Line) keep = Math.Max(keep, position.Word + 1); // слово под курсором не отбрасываем
            for (int wi = 0; wi < keep; wi++)
            {
                if (wi > 0) sb.Append(' ');
                if (li == position.Line && wi == position.Word)
                {
                    wordOffset = sb.Length;
                    wordLength = words[wi].Text.Length;
                }
                sb.Append(words[wi].Text);
            }
        }
        return new OcrContext(sb.ToString(), wordOffset, wordLength);
    }

    /// <summary>
    /// Строка — имя говорящего («Dana's mom:», «Marlowe:»): короткая и с двоеточием на конце. Она не часть реплики и
    /// не должна склеиваться с ней в предложение.
    /// </summary>
    internal static bool IsSpeakerLabel(OcrLineBox line)
    {
        if (line.Words.Count is 0 or > 4) return false;
        string last = line.Words[^1].Text.TrimEnd();
        return last.Length > 1 && last[^1] == ':';
    }

    /// <summary>
    /// Сколько слов строки оставить: в конце строки после знака конца предложения часто оказывается значок
    /// (сердце, пончик), который OCR читает как «o», «0», «•», «j». Такие хвосты отбрасываются.
    /// </summary>
    internal static int KeptWordCount(IReadOnlyList<OcrWordBox> words)
    {
        int keep = words.Count;
        while (keep >= 2 && IsJunkToken(words[keep - 1].Text) && EndsSentence(words[keep - 2].Text)) keep--;
        return keep;
    }

    private static bool EndsSentence(string text)
    {
        string t = text.TrimEnd('\'', '"', '’', '”', ')');
        return t.Length > 0 && t[^1] is '.' or '!' or '?' or '…';
    }

    /// <summary>Не слово: без букв, одна буква кроме «I»/«a» или короткий набор с цифрой.</summary>
    private static bool IsJunkToken(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return true;
        if (!t.Any(char.IsLetter)) return true;
        if (t.Length == 1) return !"IiAa".Contains(t[0]);
        return t.Length <= 2 && t.Any(char.IsDigit);
    }

    /// <summary>
    /// Нужна ли область шире. Если строка абзаца со словом доходит до левого или правого края снимка
    /// (текст оборван на границе: «…she's too f»), возвращается область на всю ширину <paramref name="bounds"/>
    /// (монитора или кадра) с той же высотой; иначе null. Слово под точкой не найдено — тоже null.
    /// </summary>
    public static ScreenRect? WiderRegion(IReadOnlyList<OcrLineBox> lines, ScreenPoint point, ScreenRect region, ScreenRect bounds, double tolerance)
    {
        bool canGrowLeft = region.X > bounds.X + 1, canGrowRight = region.Right < bounds.Right - 1;
        if (!canGrowLeft && !canGrowRight) return null;
        if (FindWord(lines, point.X, point.Y, tolerance) is not { } position) return null;

        foreach (int li in ContextBlock(lines, position))
        {
            var line = lines[li];
            double margin = Math.Max(4, line.Height * 0.5);
            bool cutLeft = canGrowLeft && line.Bounds.X <= region.X + margin;
            bool cutRight = canGrowRight && line.Bounds.Right >= region.Right - margin;
            if (cutLeft || cutRight) return new ScreenRect(bounds.X, region.Y, bounds.Width, region.Height);
        }
        return null;
    }

    /// <summary>Предложение со словом в позиции <paramref name="position"/>.</summary>
    public static string FindSentence(IReadOnlyList<OcrLineBox> lines, WordPosition position) =>
        FindSentence(lines, position, out _);

    /// <param name="wordOffset">Смещение слова в предложении.</param>
    public static string FindSentence(IReadOnlyList<OcrLineBox> lines, WordPosition position, out int wordOffset)
    {
        var context = BuildContext(lines, position);
        return SentenceFinder.Find(context.Text, context.WordOffset, out wordOffset);
    }

    private static int? FindNeighbor(IReadOnlyList<OcrLineBox> lines, int index, bool up)
    {
        var line = lines[index];
        double h = line.Height;
        if (h <= 0) return null;

        int? best = null;
        double bestGap = double.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i == index) continue;
            if (IsNeighbor(line, lines[i], up, out double gap) && gap < bestGap)
            {
                best = i;
                bestGap = gap;
            }
        }
        return best;
    }

    /// <summary>
    /// <paramref name="other"/> — соседняя строка того же абзаца сверху/снизу от <paramref name="line"/>:
    /// небольшой межстрочный зазор, похожая высота шрифта, перекрытие по горизонтали.
    /// </summary>
    private static bool IsNeighbor(OcrLineBox line, OcrLineBox other, bool up, out double gap)
    {
        double h = line.Height;
        // Вертикальный зазор между строками (отрицательный — строки накладываются).
        gap = up ? line.Bounds.Y - other.Bounds.Bottom : other.Bounds.Y - line.Bounds.Bottom;
        if (h <= 0) return false;
        bool rightSide = up ? other.Bounds.CenterY < line.Bounds.CenterY : other.Bounds.CenterY > line.Bounds.CenterY;
        if (!rightSide || gap > h * 1.2 || gap < -h * 0.5) return false;

        double ratio = other.Height / h;
        if (ratio is < 0.65 or > 1.5) return false; // другой размер шрифта — заголовок/подпись

        double overlap = Math.Min(line.Bounds.Right, other.Bounds.Right) - Math.Max(line.Bounds.X, other.Bounds.X);
        return overlap > 0; // иначе соседняя колонка
    }

    /// <summary>
    /// Весь текст выделенной области: строки сверху вниз, строки одного абзаца склеиваются через пробел
    /// (с переносами по слогам), абзацы разделяются переводом строки.
    /// </summary>
    public static string BuildText(IReadOnlyList<OcrLineBox> lines)
    {
        var ordered = lines.Where(l => l.Words.Count > 0).OrderBy(l => l.Bounds.CenterY).ThenBy(l => l.Bounds.X).ToList();
        var sb = new StringBuilder();
        OcrLineBox? previous = null;
        foreach (var line in ordered)
        {
            string text = string.Join(' ', line.Words.Select(w => w.Text));
            if (previous is not null)
            {
                if (!IsNeighbor(previous, line, up: false, out _)) sb.Append('\n');
                else if (sb[^1] == '-' && char.IsLower(text[0])) sb.Length--; // «inter-» + «national»
                else sb.Append(' ');
            }
            sb.Append(text);
            previous = line;
        }
        return sb.ToString();
    }
}
