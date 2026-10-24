using System.Diagnostics;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HoverTranslate.Core.TextSource;

/// <summary>
/// Запасной движок OCR: PaddleOCR PP-OCRv4 на ONNX Runtime (детектор текста DB + распознаватель CTC), без OpenCV.
/// Медленнее Windows OCR (0,5–1,5 с против ~0,1 с), но читает то, что тот не читает: стилизованные и наклонные шрифты,
/// текст поверх иллюстраций со светлыми линиями. Нужен, когда Windows OCR дал много несловарных слов.
/// Модели лежат в <see cref="ModelDir"/> (tools/DataBuilder/export_ppocr.py); без них движок недоступен.
/// </summary>
public sealed class PaddleOcr : IDisposable
{
    /// <summary>Папка с det.onnx и rec.onnx.</summary>
    public static string ModelDir { get; } = Path.Combine(AppPaths.DataDir, "models", "ppocr");

    public static bool ModelsExist(string dir) =>
        File.Exists(Path.Combine(dir, "det.onnx")) && File.Exists(Path.Combine(dir, "rec.onnx"));

    // Детектор: короткая сторона растягивается до DetMinSide (как в PaddleOCR по умолчанию), но длинная не больше
    // DetMaxSide. 480 вместо 736 быстрее на треть, но иконки интерфейса сливаются с подписями («Ssize» вместо «Size»).
    internal const int DetMinSide = 736, DetMaxSide = 2560;
    internal const float DetThreshold = 0.3f, BoxThreshold = 0.5f, UnclipRatio = 1.6f;
    internal const int MinBoxSide = 3;
    // Распознаватель: высота строки 48, ширина не меньше 320 (короткая строка добивается серым).
    internal const int RecHeight = 48, RecMinWidth = 320;
    internal const double MinLineScore = 0.5;

    private readonly InferenceSession _det, _rec;
    private readonly string _detInput, _recInput;
    private readonly string[] _chars; // индекс класса -> символ: 0 — «пусто» CTC, последний — пробел
    private readonly bool[] _allowed; // что допускаем в ответе: латиница и пунктуация, без иероглифов

    /// <summary>Длительность последнего распознавания: детектор, распознавание.</summary>
    public (long DetMs, long RecMs) LastTimings { get; private set; }

    public PaddleOcr(string modelDir, int? threads = null)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = threads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 6),
        };
        // Без активного ожидания: иначе потоки ORT крутятся вхолостую и отнимают CPU у игры (как в MarianModel).
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

        var det = Task.Run(() => new InferenceSession(Path.Combine(modelDir, "det.onnx"), options));
        var rec = Task.Run(() => new InferenceSession(Path.Combine(modelDir, "rec.onnx"), options));
        try
        {
            Task.WaitAll(det, rec);
        }
        catch
        {
            foreach (var task in new[] { det, rec })
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
            throw;
        }
        _det = det.Result;
        _rec = rec.Result;
        _detInput = _det.InputMetadata.Keys.First();
        _recInput = _rec.InputMetadata.Keys.First();

        // Список символов распознавателя лежит в метаданных модели: по строке на символ.
        string keys = _rec.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? c) ? c
            : throw new InvalidDataException("В rec.onnx нет списка символов (метаданные «character»)");
        _chars = ["blank", .. keys.Split('\n'), " "];
        _allowed = BuildAllowed(_chars);
    }

    /// <summary>Строки со словами в координатах экрана; <paramref name="region"/> — где на экране снят <paramref name="image"/>.</summary>
    public IReadOnlyList<OcrLineBox> Recognize(BgraImage image, ScreenRect region, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var boxes = Detect(image);
        long detMs = sw.ElapsedMilliseconds;

        var lines = new List<OcrLineBox>();
        foreach (var box in boxes)
        {
            ct.ThrowIfCancellationRequested();
            if (box.Height >= box.Width * 1.5) continue; // вертикальный текст не поддерживаем
            var words = RecognizeLine(image, box);
            if (words.Count == 0) continue;
            lines.Add(new OcrLineBox([.. words.Select(w => new OcrWordBox(w.Text,
                new ScreenRect(region.X + w.Left, region.Y + box.Top, w.Right - w.Left, box.Height)))]));
        }
        LastTimings = (detMs, sw.ElapsedMilliseconds - detMs);
        return [.. lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X)];
    }

    /// <summary>Рамка строки текста в пикселях снимка.</summary>
    internal readonly record struct TextBox(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>Слово строки: текст и горизонтальные границы в пикселях снимка.</summary>
    internal readonly record struct LineWord(string Text, double Left, double Right);

    // ------------------------------------------------------------------ детектор

    private List<TextBox> Detect(BgraImage image)
    {
        var (w, h) = DetSize(image.Width, image.Height);
        var input = ToTensor(image, new TextBox(0, 0, image.Width, image.Height), w, h, w);
        using var results = _det.Run([NamedOnnxValue.CreateFromTensor(_detInput, new DenseTensor<float>(input, [1, 3, h, w]))]);
        var prob = results[0].AsTensor<float>().ToArray();
        return BoxesFromMap(prob, w, h, image.Width, image.Height);
    }

    /// <summary>Размер входа детектора: кратен 32, короткая сторона не меньше <see cref="DetMinSide"/>, длинная не больше <see cref="DetMaxSide"/>.</summary>
    internal static (int Width, int Height) DetSize(int width, int height)
    {
        double ratio = Math.Min(width, height) < DetMinSide ? DetMinSide / (double)Math.Min(width, height) : 1;
        if (Math.Max(width, height) * ratio > DetMaxSide) ratio = DetMaxSide / (double)Math.Max(width, height);
        static int Round32(double v) => Math.Max(32, (int)Math.Round(v / 32) * 32);
        return (Round32(width * ratio), Round32(height * ratio));
    }

    /// <summary>
    /// Карта вероятностей текста -> рамки строк (как DBPostProcess в PaddleOCR): порог 0,3, расширение 2×2, связные
    /// области, средняя вероятность в рамке не меньше 0,5, рамка расширяется (unclip) и переводится в пиксели снимка.
    /// Рамки осевые: экранный текст горизонтален, повёрнутый прямоугольник не нужен.
    /// </summary>
    internal static List<TextBox> BoxesFromMap(float[] prob, int mapWidth, int mapHeight, int destWidth, int destHeight)
    {
        var mask = new bool[prob.Length];
        for (int i = 0; i < prob.Length; i++) mask[i] = prob[i] > DetThreshold;
        // cv2.dilate с ядром 2×2: пиксель включается, если включён он сам или сосед слева, сверху или по диагонали.
        var dilated = new bool[prob.Length];
        for (int y = 0; y < mapHeight; y++)
            for (int x = 0; x < mapWidth; x++)
                dilated[y * mapWidth + x] = mask[y * mapWidth + x]
                    || (x > 0 && mask[y * mapWidth + x - 1])
                    || (y > 0 && mask[(y - 1) * mapWidth + x])
                    || (x > 0 && y > 0 && mask[(y - 1) * mapWidth + x - 1]);

        var boxes = new List<TextBox>();
        var seen = new bool[prob.Length];
        var stack = new Stack<int>();
        for (int start = 0; start < dilated.Length; start++)
        {
            if (!dilated[start] || seen[start]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % mapWidth, py = p / mapWidth;
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= mapWidth || ny >= mapHeight) continue;
                        int n = ny * mapWidth + nx;
                        if (!dilated[n] || seen[n]) continue;
                        seen[n] = true;
                        stack.Push(n);
                    }
            }

            int bw = maxX - minX, bh = maxY - minY; // как у minAreaRect по центрам пикселей контура
            if (Math.Min(bw, bh) < MinBoxSide) continue;
            if (MeanProb(prob, mapWidth, minX, minY, maxX, maxY) < BoxThreshold) continue;

            double distance = bw * (double)bh * UnclipRatio / (2.0 * (bw + bh));
            if (Math.Min(bw, bh) + 2 * distance < MinBoxSide + 2) continue;
            double sx = destWidth / (double)mapWidth, sy = destHeight / (double)mapHeight;
            int left = (int)Math.Clamp(Math.Round((minX - distance) * sx), 0, destWidth);
            int right = (int)Math.Clamp(Math.Round((maxX + distance) * sx), 0, destWidth);
            int top = (int)Math.Clamp(Math.Round((minY - distance) * sy), 0, destHeight);
            int bottom = (int)Math.Clamp(Math.Round((maxY + distance) * sy), 0, destHeight);
            if (right - left > 0 && bottom - top > 0) boxes.Add(new TextBox(left, top, right, bottom));
            if (boxes.Count >= 1000) break;
        }
        return boxes;
    }

    private static double MeanProb(float[] prob, int width, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                sum += prob[y * width + x];
        return sum / ((x1 - x0 + 1) * (double)(y1 - y0 + 1));
    }

    // ------------------------------------------------------------------ распознаватель

    private List<LineWord> RecognizeLine(BgraImage image, TextBox box)
    {
        int resizedW = Math.Max(1, (int)Math.Ceiling(RecHeight * box.Width / (double)box.Height));
        int inputW = Math.Max(RecMinWidth, resizedW);
        var input = ToTensor(image, box, resizedW, RecHeight, inputW);
        using var results = _rec.Run([NamedOnnxValue.CreateFromTensor(_recInput, new DenseTensor<float>(input, [1, 3, RecHeight, inputW]))]);
        var output = results[0].AsTensor<float>();
        int steps = output.Dimensions[1], classes = output.Dimensions[2];
        if (classes != _chars.Length)
            throw new InvalidDataException($"Распознаватель вернул {classes} классов, в метаданных {_chars.Length} символов");

        var chars = Decode(output.ToArray(), steps, classes, _allowed, _chars, out double score);
        if (chars.Count == 0 || score < MinLineScore) return [];
        // Шаг по времени -> пиксели снимка: вход распознавателя шире текста на серое поле справа.
        double stepWidth = inputW / (double)steps * box.Width / resizedW;
        var words = SplitWords(chars, stepWidth);
        return [.. words.Select(w => w with
        {
            Left = box.Left + Math.Clamp(w.Left, 0, box.Width),
            Right = box.Left + Math.Clamp(w.Right, 0, box.Width),
        }).Where(w => w.Right > w.Left)];
    }

    /// <summary>
    /// Ответ CTC -> символы с номером шага: в каждом шаге лучший допустимый класс, повторы подряд и «пусто» убираются.
    /// <paramref name="score"/> — средняя уверенность по оставшимся символам.
    /// </summary>
    internal static List<(char Ch, int Step)> Decode(float[] logits, int steps, int classes, bool[] allowed, string[] chars, out double score)
    {
        var result = new List<(char, int)>();
        double sum = 0;
        int previous = -1;
        for (int t = 0; t < steps; t++)
        {
            int best = 0;
            float bestValue = float.NegativeInfinity;
            int offset = t * classes;
            for (int c = 0; c < classes; c++)
            {
                if (!allowed[c] || logits[offset + c] <= bestValue) continue;
                best = c;
                bestValue = logits[offset + c];
            }
            if (best != previous && best != 0)
            {
                result.Add((chars[best][0], t));
                sum += bestValue;
            }
            previous = best;
        }
        score = result.Count == 0 ? 0 : sum / result.Count;
        return result;
    }

    /// <summary>
    /// Слова из символов строки: граница слова — пробел. Границы слова по шагам CTC: пик символа приходится на его
    /// середину, поэтому от первого и последнего символа слова откладывается полшага между символами слова
    /// (для односимвольного слова — два шага). Соседние слова не перекрываются.
    /// </summary>
    internal static List<LineWord> SplitWords(IReadOnlyList<(char Ch, int Step)> chars, double stepWidth)
    {
        var words = new List<LineWord>();
        var text = new System.Text.StringBuilder();
        int first = -1, last = -1;

        void Flush()
        {
            if (text.Length == 0) return;
            double spacing = text.Length > 1 ? (last - first) / (double)(text.Length - 1) : 4;
            double left = (first + 0.5 - spacing / 2) * stepWidth, right = (last + 0.5 + spacing / 2) * stepWidth;
            if (words.Count > 0 && left < words[^1].Right)
            {
                double middle = (left + words[^1].Right) / 2;
                words[^1] = words[^1] with { Right = middle };
                left = middle;
            }
            words.Add(new LineWord(text.ToString(), left, right));
            text.Clear();
            first = -1;
        }

        foreach (var (ch, step) in chars)
        {
            if (ch == ' ')
            {
                Flush();
                continue;
            }
            if (text.Length == 0) first = step;
            last = step;
            text.Append(ch);
        }
        Flush();
        return words;
    }

    /// <summary>Допустимые классы: «пусто», пробел, печатный ASCII и типографские кавычки/тире; иероглифы и прочее исключаются.</summary>
    internal static bool[] BuildAllowed(string[] chars)
    {
        var allowed = new bool[chars.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            if (i == 0 || i == chars.Length - 1) { allowed[i] = true; continue; }
            if (chars[i].Length != 1) continue;
            char c = chars[i][0];
            allowed[i] = c is >= '!' and <= '~' || c is '’' or '‘' or '“' or '”' or '…' or '–' or '—';
        }
        return allowed;
    }

    // ------------------------------------------------------------------ картинка -> тензор

    /// <summary>
    /// Вырезка <paramref name="box"/> из BGRA-снимка, билинейный ресайз до <paramref name="width"/>×<paramref name="height"/>
    /// и тензор [3, height, padWidth] в порядке BGR, (x/255 − 0,5)/0,5; справа нули (серое поле).
    /// </summary>
    internal static float[] ToTensor(BgraImage image, TextBox box, int width, int height, int padWidth)
    {
        var tensor = new float[3 * height * padWidth];
        int cw = Math.Max(1, box.Width), ch = Math.Max(1, box.Height);
        double sx = cw / (double)width, sy = ch / (double)height;
        var px = image.Pixels;
        for (int y = 0; y < height; y++)
        {
            double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, ch - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, ch - 1);
            float wy = (float)(fy - y0);
            for (int x = 0; x < width; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, cw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, cw - 1);
                float wx = (float)(fx - x0);
                int p00 = ((box.Top + y0) * image.Width + box.Left + x0) * 4, p01 = ((box.Top + y0) * image.Width + box.Left + x1) * 4;
                int p10 = ((box.Top + y1) * image.Width + box.Left + x0) * 4, p11 = ((box.Top + y1) * image.Width + box.Left + x1) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float top = px[p00 + c] * (1 - wx) + px[p01 + c] * wx;
                    float bottom = px[p10 + c] * (1 - wx) + px[p11 + c] * wx;
                    tensor[(c * height + y) * padWidth + x] = (top * (1 - wy) + bottom * wy) / 127.5f - 1f;
                }
            }
        }
        return tensor;
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
    }
}
