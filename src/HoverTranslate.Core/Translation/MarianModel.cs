using Microsoft.ML.OnnxRuntime;

namespace HoverTranslate.Core.Translation;

/// <summary>
/// opus-mt-en-ru в ONNX (int8): энкодер, декодер первого шага и декодер с KV-кэшем для остальных шагов.
/// Greedy-декодирование — тот же алгоритм, что проверен в tools/DataBuilder/export_model.py; beam search —
/// как в Hugging Face (generation_config: num_beams 4, length_penalty 1,0), гипотезы идут одним батчем.
/// Логит &lt;pad&gt; маскируется на каждом шаге (bad_words_ids из generation_config.json).
/// </summary>
public sealed class MarianModel : IDisposable
{
    private const string Logits = "logits";

    /// <summary>Ширина луча по умолчанию: на 51 фразе даёт +1,7 chrF к greedy.</summary>
    public const int DefaultBeams = 4;

    private const float LogSumExpCutoff = 25f;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _decoderWithPast;
    private readonly int _startId;
    private readonly int _eosId;
    private readonly int _padId;
    private readonly string[] _pastInputs;

    /// <summary>
    /// Потоков на один прогон. Шаг декодера с batch 1 — мелкие матрицы: больше 2–4 потоков только мешают
    /// (16 логических ядер: 4 потока ~180 мс, все ядра ~290 мс на предложение из 30 слов).
    /// </summary>
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public MarianModel(string modelDir, int decoderStartId, int eosId, int padId, int? threads = null)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads ?? DefaultThreads,
        };
        // Без активного ожидания: иначе после каждого перевода потоки ORT крутятся вхолостую и отнимают CPU у игры.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

        // Три сессии грузятся параллельно: это основная часть времени запуска модели.
        var encoder = Task.Run(() => new InferenceSession(Path.Combine(modelDir, "encoder_model.onnx"), options));
        var decoder = Task.Run(() => new InferenceSession(Path.Combine(modelDir, "decoder_model.onnx"), options));
        var withPast = Task.Run(() => new InferenceSession(Path.Combine(modelDir, "decoder_with_past_model.onnx"), options));
        try
        {
            Task.WaitAll(encoder, decoder, withPast);
        }
        catch
        {
            foreach (var task in new[] { encoder, decoder, withPast })
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
            throw;
        }
        _encoder = encoder.Result;
        _decoder = decoder.Result;
        _decoderWithPast = withPast.Result;
        _startId = decoderStartId;
        _eosId = eosId;
        _padId = padId;
        _pastInputs = _decoderWithPast.InputNames.Where(n => n.StartsWith("past_key_values.", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Greedy-перевод; id без &lt;/s&gt;. Отмена проверяется между шагами декодера.</summary>
    public int[] Generate(int[] inputIds, CancellationToken ct) => Generate(inputIds, 1, ct);

    /// <summary>
    /// Id перевода (без &lt;/s&gt;): greedy при <paramref name="beams"/> ≤ 1, иначе beam search заданной ширины.
    /// Отмена проверяется между шагами декодера.
    /// </summary>
    public int[] Generate(int[] inputIds, int beams, CancellationToken ct) =>
        beams <= 1 ? GenerateGreedy(inputIds, ct) : GenerateBeam(inputIds, beams, ct);

    private int[] GenerateGreedy(int[] inputIds, CancellationToken ct)
    {
        int n = inputIds.Length;
        int maxNew = Math.Min(512, 2 * n + 16);
        long[] shape = [1, n];
        using var ids = OrtValue.CreateTensorValueFromMemory(inputIds.Select(i => (long)i).ToArray(), shape);
        using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, n).ToArray(), shape);
        using var runOptions = new RunOptions();

        using var encoded = _encoder.Run(runOptions,
            new Dictionary<string, OrtValue> { ["input_ids"] = ids, ["attention_mask"] = mask }, _encoder.OutputNames);
        ct.ThrowIfCancellationRequested();

        // Шаг 0: декодер без кэша. Его present.*.encoder.* (кэш внимания на энкодер) не меняется до конца.
        using var start = OrtValue.CreateTensorValueFromMemory(new long[] { _startId }, [1, 1]);
        using var first = _decoder.Run(runOptions, new Dictionary<string, OrtValue>
        {
            ["encoder_attention_mask"] = mask,
            ["input_ids"] = start,
            ["encoder_hidden_states"] = encoded[0],
        }, _decoder.OutputNames);

        var past = new Dictionary<string, OrtValue>(StringComparer.Ordinal);
        CollectPresents(_decoder.OutputNames, first, past);

        var result = new List<int>();
        IDisposableReadOnlyCollection<OrtValue>? previous = null;
        try
        {
            var logits = first[IndexOf(_decoder.OutputNames, Logits)];
            for (int step = 0; step < maxNew; step++)
            {
                int token = ArgMaxLastRow(logits);
                if (token == _eosId) break;
                result.Add(token);
                ct.ThrowIfCancellationRequested();

                using var next = OrtValue.CreateTensorValueFromMemory(new long[] { token }, [1, 1]);
                var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
                {
                    ["encoder_attention_mask"] = mask,
                    ["input_ids"] = next,
                };
                foreach (string name in _pastInputs) feeds[name] = past[name];

                var outputs = _decoderWithPast.Run(runOptions, feeds, _decoderWithPast.OutputNames);
                // Обновляется только self-attention кэш декодера; предыдущий шаг больше не нужен.
                CollectPresents(_decoderWithPast.OutputNames, outputs, past);
                previous?.Dispose();
                previous = outputs;
                logits = outputs[IndexOf(_decoderWithPast.OutputNames, Logits)];
            }
        }
        finally
        {
            previous?.Dispose();
        }
        return result.ToArray();
    }

    /// <summary>
    /// Beam search: <paramref name="beams"/> гипотез одним батчем. Энкодер считается один раз и размножается;
    /// на каждом шаге из <c>beams × словарь</c> берутся лучшие кандидаты (EOS уходит в готовые гипотезы, если он
    /// в первых <paramref name="beams"/>), а KV-кэш self-attention переставляется по выбранным родителям
    /// (кэш на энкодер одинаков у всех гипотез и не переставляется). Оценка готовой гипотезы — сумма логарифмов
    /// вероятностей, делённая на длину (с EOS); остановка — как в HF при early_stopping=false.
    /// </summary>
    private int[] GenerateBeam(int[] inputIds, int beams, CancellationToken ct)
    {
        int n = inputIds.Length, width = beams;
        int maxNew = Math.Min(512, 2 * n + 16);
        using var runOptions = new RunOptions();

        using var ids = OrtValue.CreateTensorValueFromMemory(inputIds.Select(i => (long)i).ToArray(), [1, n]);
        using var mask1 = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, n).ToArray(), [1, n]);
        using var encoded = _encoder.Run(runOptions,
            new Dictionary<string, OrtValue> { ["input_ids"] = ids, ["attention_mask"] = mask1 }, _encoder.OutputNames);
        ct.ThrowIfCancellationRequested();

        int hidden = (int)encoded[0].GetTensorTypeAndShape().Shape[2];
        var tiled = new float[width * n * hidden];
        for (int b = 0; b < width; b++)
            encoded[0].GetTensorDataAsSpan<float>().CopyTo(tiled.AsSpan(b * n * hidden, n * hidden));
        using var encodedBatch = OrtValue.CreateTensorValueFromMemory(tiled, [width, n, hidden]);
        using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, width * n).ToArray(), [width, n]);

        using var start = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat((long)_startId, width).ToArray(), [width, 1]);
        using var first = _decoder.Run(runOptions, new Dictionary<string, OrtValue>
        {
            ["encoder_attention_mask"] = mask,
            ["input_ids"] = start,
            ["encoder_hidden_states"] = encodedBatch,
        }, _decoder.OutputNames);

        var past = new Dictionary<string, OrtValue>(StringComparer.Ordinal);
        CollectPresents(_decoder.OutputNames, first, past);

        // Сначала живая только гипотеза 0: остальные стартуют с -inf, иначе в луче окажутся копии одной и той же.
        var tokens = new List<int>[width];
        var scores = new double[width];
        for (int b = 0; b < width; b++)
        {
            tokens[b] = [];
            scores[b] = b == 0 ? 0 : double.NegativeInfinity;
        }
        var finished = new List<(double Score, int[] Tokens)>();
        bool done = false;

        IDisposableReadOnlyCollection<OrtValue>? previous = null;
        var reordered = new List<OrtValue>();
        try
        {
            var logits = first[IndexOf(_decoder.OutputNames, Logits)];
            for (int step = 0; step < maxNew && !done; step++)
            {
                var candidates = TopCandidates(logits, scores, width);
                var next = new List<Candidate>(width);
                for (int rank = 0; rank < candidates.Count && next.Count < width; rank++)
                {
                    var c = candidates[rank];
                    if (c.Token == _eosId)
                    {
                        if (rank < width)
                        {
                            finished.Add((c.Score / (tokens[c.Beam].Count + 1), [.. tokens[c.Beam]]));
                            finished.Sort((a, b) => b.Score.CompareTo(a.Score));
                            if (finished.Count > width) finished.RemoveAt(width);
                        }
                    }
                    else
                    {
                        next.Add(c);
                    }
                }
                if (next.Count < width)
                {
                    done = true; // не из чего продолжать: все лучшие кандидаты закончились
                    break;
                }
                if (finished.Count >= width && next.Max(c => c.Score) / (step + 1) <= finished[^1].Score)
                {
                    done = true;
                    break;
                }

                var sources = next.Select(c => c.Beam).ToArray();
                tokens = next.Select(c => (List<int>)[.. tokens[c.Beam], c.Token]).ToArray();
                scores = next.Select(c => c.Score).ToArray();
                ct.ThrowIfCancellationRequested();

                using var nextIds = OrtValue.CreateTensorValueFromMemory(next.Select(c => (long)c.Token).ToArray(), [width, 1]);
                var feeds = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
                {
                    ["encoder_attention_mask"] = mask,
                    ["input_ids"] = nextIds,
                };
                var oldReordered = reordered;
                reordered = [];
                bool identity = sources.Select((s, i) => s == i).All(x => x);
                foreach (string name in _pastInputs)
                {
                    if (identity || !name.Contains(".decoder.", StringComparison.Ordinal))
                    {
                        feeds[name] = past[name];
                        continue;
                    }
                    var moved = Gather(past[name], sources);
                    reordered.Add(moved);
                    feeds[name] = moved;
                }

                var outputs = _decoderWithPast.Run(runOptions, feeds, _decoderWithPast.OutputNames);
                CollectPresents(_decoderWithPast.OutputNames, outputs, past);
                previous?.Dispose();
                foreach (var old in oldReordered) old.Dispose();
                previous = outputs;
                logits = outputs[IndexOf(_decoderWithPast.OutputNames, Logits)];
            }
        }
        finally
        {
            previous?.Dispose();
            foreach (var value in reordered) value.Dispose();
        }

        if (!done || finished.Count == 0)
        {
            // Дошли до предела длины: недописанные гипотезы тоже участвуют, как в HF.
            for (int b = 0; b < width; b++)
                if (!double.IsNegativeInfinity(scores[b]))
                    finished.Add((scores[b] / Math.Max(1, tokens[b].Count), [.. tokens[b]]));
        }
        return finished.OrderByDescending(f => f.Score).First().Tokens;
    }

    private readonly record struct Candidate(double Score, int Beam, int Token);

    /// <summary>
    /// Лучшие <c>2 × width</c> продолжений по всем гипотезам: накопленная оценка + log-softmax шага
    /// (с замаскированным &lt;pad&gt;, как renormalize_logits в generation_config). Мёртвые гипотезы (-inf) пропускаются.
    /// </summary>
    private List<Candidate> TopCandidates(OrtValue logits, double[] scores, int width)
    {
        int vocab = (int)logits.GetTensorTypeAndShape().Shape[^1];
        var data = logits.GetTensorDataAsSpan<float>(); // [width, 1, vocab]
        int take = 2 * width;
        var all = new List<Candidate>(take * width);
        var top = new List<Candidate>(take + 1);
        for (int b = 0; b < width; b++)
        {
            if (double.IsNegativeInfinity(scores[b])) continue;
            var row = data.Slice(b * vocab, vocab);
            float max = float.NegativeInfinity;
            for (int i = 0; i < vocab; i++)
                if (i != _padId && row[i] > max) max = row[i];
            // Слагаемые ниже max - 25 меньше e^-25 (~1e-11) и на сумму не влияют; без отсева Math.Exp по всему словарю
            // (62 тыс. × гипотезы × шаги) съедал ~100 мс на предложение.
            double sum = 0;
            float cutoff = max - LogSumExpCutoff;
            for (int i = 0; i < vocab; i++)
                if (row[i] > cutoff && i != _padId) sum += Math.Exp(row[i] - max);
            double offset = scores[b] - (max + Math.Log(sum));

            top.Clear();
            double threshold = double.NegativeInfinity;
            for (int i = 0; i < vocab; i++)
            {
                if (i == _padId || row[i] <= threshold && top.Count >= take) continue;
                double score = offset + row[i];
                if (top.Count >= take && score <= top[^1].Score) continue;
                int at = top.Count;
                while (at > 0 && top[at - 1].Score < score) at--;
                top.Insert(at, new Candidate(score, b, i));
                if (top.Count > take) top.RemoveAt(take);
                if (top.Count >= take) threshold = top[^1].Score - offset;
            }
            all.AddRange(top);
        }
        all.Sort((a, b) => b.Score.CompareTo(a.Score));
        return all.Count > take ? all.GetRange(0, take) : all;
    }

    /// <summary>Строки батча <paramref name="value"/> [B, ...] в порядке <paramref name="sources"/> (копия).</summary>
    private static OrtValue Gather(OrtValue value, int[] sources)
    {
        var shape = value.GetTensorTypeAndShape().Shape;
        var data = value.GetTensorDataAsSpan<float>();
        int row = data.Length / (int)shape[0];
        var copy = new float[data.Length];
        for (int i = 0; i < sources.Length; i++)
            data.Slice(sources[i] * row, row).CopyTo(copy.AsSpan(i * row, row));
        return OrtValue.CreateTensorValueFromMemory(copy, shape);
    }

    /// <summary>present.N.{decoder|encoder}.{key|value} -> past_key_values.N.{...}</summary>
    private static void CollectPresents(IReadOnlyList<string> names, IReadOnlyList<OrtValue> values, Dictionary<string, OrtValue> past)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].StartsWith("present.", StringComparison.Ordinal))
                past["past_key_values." + names[i]["present.".Length..]] = values[i];
        }
    }

    private int ArgMaxLastRow(OrtValue logits)
    {
        var shape = logits.GetTensorTypeAndShape().Shape; // [1, seq, vocab]
        int vocab = (int)shape[^1];
        var data = logits.GetTensorDataAsSpan<float>();
        var row = data[^vocab..];
        int best = -1;
        float bestValue = float.NegativeInfinity;
        for (int i = 0; i < row.Length; i++)
        {
            if (i == _padId) continue;
            if (row[i] > bestValue)
            {
                bestValue = row[i];
                best = i;
            }
        }
        return best < 0 ? _eosId : best;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i] == name) return i;
        throw new InvalidDataException($"У модели нет выхода {name}");
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
    }
}
