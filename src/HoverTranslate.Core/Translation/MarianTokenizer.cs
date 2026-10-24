using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace HoverTranslate.Core.Translation;

/// <summary>
/// Токенизатор Marian (opus-mt): текст режется на куски SentencePiece моделью source.spm, а куски
/// переводятся в id через общий vocab.json (не через id из .spm). В конце — &lt;/s&gt;.
/// Обратно: id -> куски словаря -> склейка с заменой «▁» на пробел (как decode_pieces у target.spm).
/// </summary>
public sealed class MarianTokenizer
{
    private const char SpaceMarker = '▁';

    private readonly SentencePieceTokenizer _source;
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _pieces;

    public int EosId { get; }
    public int PadId { get; }
    public int UnkId { get; }
    public int VocabSize => _pieces.Length;

    /// <summary>Предел длины входа в токенах (позиционные эмбеддинги — 512, с запасом на &lt;/s&gt;).</summary>
    public const int MaxInputTokens = 500;

    public MarianTokenizer(string modelDir)
    {
        using (var spm = File.OpenRead(Path.Combine(modelDir, "source.spm")))
            _source = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);

        using (var json = File.OpenRead(Path.Combine(modelDir, "vocab.json")))
            _vocab = JsonSerializer.Deserialize(json, TranslationJsonContext.Default.DictionaryStringInt32)
                     ?? throw new InvalidDataException("vocab.json пуст");

        _pieces = new string[_vocab.Values.Max() + 1];
        foreach (var (piece, id) in _vocab) _pieces[id] = piece;
        EosId = _vocab["</s>"];
        PadId = _vocab["<pad>"];
        UnkId = _vocab["<unk>"];
    }

    /// <summary>Куски SentencePiece (для тестов и отладки).</summary>
    public IReadOnlyList<string> Pieces(string text) =>
        _source.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false)
            .Select(t => t.Value).ToList();

    /// <summary>Id для энкодера: куски -> vocab.json, неизвестные -> &lt;unk&gt;, в конце &lt;/s&gt;.</summary>
    public int[] Encode(string text)
    {
        var pieces = Pieces(text);
        int count = Math.Min(pieces.Count, MaxInputTokens);
        var ids = new int[count + 1];
        for (int i = 0; i < count; i++)
            ids[i] = _vocab.TryGetValue(pieces[i], out int id) ? id : UnkId;
        ids[count] = EosId;
        return ids;
    }

    /// <summary>Текст из id декодера; служебные токены пропускаются.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (int id in ids)
        {
            if (id == EosId || id == PadId || id == UnkId || (uint)id >= (uint)_pieces.Length) continue;
            sb.Append(_pieces[id]);
        }
        sb.Replace(SpaceMarker, ' ');
        return sb.ToString().Trim();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class TranslationJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
