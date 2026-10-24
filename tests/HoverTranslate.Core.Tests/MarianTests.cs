using System.Diagnostics;
using System.Text.Json;
using HoverTranslate.Core.Translation;

namespace HoverTranslate.Core.Tests;

/// <summary>
/// Токенизатор и модель на настоящих данных: эталон tokenizer_golden.json пишет tools/DataBuilder/export_model.py
/// (куски и id токенизатора HF, id перевода — тот же greedy-декодер на onnxruntime в Python).
/// Без экспортированной модели тесты пропускаются.
/// </summary>
public sealed class MarianTests(ITestOutputHelper output)
{
    private static readonly Lazy<(MarianTokenizer Tokenizer, MarianModel Model)> Shared = new(() =>
    {
        var tokenizer = new MarianTokenizer(RepoData.Model!);
        return (tokenizer, new MarianModel(RepoData.Model!, tokenizer.PadId, tokenizer.EosId, tokenizer.PadId));
    });

    // Без модели — один случай -1: тест сам пропустится (пустые данные теории xUnit считает ошибкой).
    public static TheoryData<int> SourceCases() => new(Cache.Value is { } g ? Enumerable.Range(0, g.Source.Count) : [-1]);

    public static TheoryData<int> TargetCases() => new(Cache.Value is { } g ? Enumerable.Range(0, g.Target.Count) : [-1]);

    [Theory]
    [MemberData(nameof(SourceCases))]
    public void Tokenizer_MatchesHuggingFace(int index)
    {
        var golden = Golden()!.Source[index];
        var tokenizer = Shared.Value.Tokenizer;
        Assert.Equal(golden.Pieces, tokenizer.Pieces(golden.Text));
        Assert.Equal(golden.Ids, tokenizer.Encode(golden.Text));
    }

    [Theory]
    [MemberData(nameof(TargetCases))]
    public void Decode_MatchesGoldenText(int index)
    {
        var golden = Golden()!.Target[index];
        Assert.Equal(golden.Text, Shared.Value.Tokenizer.Decode(golden.Ids));
    }

    [Theory]
    [MemberData(nameof(TargetCases))]
    public void Generate_SameTokensAsPythonGreedy(int index)
    {
        var (tokenizer, model) = Shared.Value;
        var source = Golden()!.Source[index];
        var target = Golden()!.Target[index];

        var ids = model.Generate(tokenizer.Encode(source.Text), CancellationToken.None);
        output.WriteLine($"{source.Text} -> {tokenizer.Decode(ids)}");
        Assert.Equal(target.Ids, ids);
    }

    [Fact]
    public void BeamSearch_OneBeamIsGreedy()
    {
        Golden();
        var (tokenizer, model) = Shared.Value;
        var ids = tokenizer.Encode("Don't worry about it, it's not your fault.");

        Assert.Equal(model.Generate(ids, CancellationToken.None), model.Generate(ids, 1, CancellationToken.None));
    }

    [Fact]
    public void BeamSearch_TimeIsReasonable()
    {
        Golden();
        var (tokenizer, model) = Shared.Value;
        var ids = tokenizer.Encode("Wait, hold on a second. Did you hear that?");
        model.Generate(ids, 4, CancellationToken.None); // прогрев
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) model.Generate(ids, 4, CancellationToken.None);
        long greedyStart = sw.ElapsedMilliseconds;
        for (int i = 0; i < 5; i++) model.Generate(ids, CancellationToken.None);
        output.WriteLine($"beam 4: {greedyStart / 5} мс, greedy: {(sw.ElapsedMilliseconds - greedyStart) / 5} мс");
        Assert.True(greedyStart / 5 < 2000, "beam search на одном предложении дольше 2 с");
    }

    [Fact]
    public void Generate_Cancellation()
    {
        var (tokenizer, model) = Shared.Value;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => model.Generate(tokenizer.Encode("Hello, world!"), cts.Token));
    }

    [Fact]
    public void BeamSearch_Cancellation()
    {
        var (tokenizer, model) = Shared.Value;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => model.Generate(tokenizer.Encode("Hello, world!"), 4, cts.Token));
    }

    [Fact]
    public void Tokenizer_LongInputTruncated()
    {
        var tokenizer = Shared.Value.Tokenizer;
        var ids = tokenizer.Encode(string.Join(' ', Enumerable.Repeat("word", 2000)));
        Assert.Equal(MarianTokenizer.MaxInputTokens + 1, ids.Length);
        Assert.Equal(tokenizer.EosId, ids[^1]);
    }

    private static GoldenFile? Golden()
    {
        if (RepoData.Model is null)
        {
            Assert.Skip("data/models/opus-mt-en-ru не экспортирована (tools/DataBuilder/export_model.py)");
        }
        return Cache.Value;
    }

    private static readonly Lazy<GoldenFile?> Cache = new(() =>
        RepoData.Model is null ? null
            : JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(Path.Combine(RepoData.Model, "tokenizer_golden.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

    private sealed record GoldenFile(List<GoldenSource> Source, List<GoldenTarget> Target);

    private sealed record GoldenSource(string Text, List<string> Pieces, List<int> Ids);

    private sealed record GoldenTarget(List<int> Ids, string Text);
}
