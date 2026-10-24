using System.Text.Json;
using HoverTranslate.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Dictionary;

public enum MatchKind
{
    Word,
    /// <summary>Фразовый глагол или выражение из слова под курсором и соседних слов.</summary>
    Phrase,
}

/// <summary>Найденная статья и лемма, к которой она относится.</summary>
public sealed record DictionaryMatch(string Lemma, DictEntry Entry, MatchKind Kind = MatchKind.Word);

public interface IDictionaryService
{
    /// <summary>Есть ли база словаря.</summary>
    bool IsAvailable { get; }

    /// <summary>Статья для отдельного слова (любая форма) без контекста. null — слова нет в словаре.</summary>
    DictionaryMatch? Lookup(string word);

    /// <summary>Частотность (zipf) слова в любой форме; null — слова нет в словаре. Дешевле <see cref="Lookup(string)"/>: статья не грузится.</summary>
    double? Zipf(string word);

    /// <summary>
    /// Статьи для слова в предложении: сначала фразы («give up», «look forward to»), затем само слово.
    /// Значения каждой статьи упорядочены по контексту. Пустой список — ничего не найдено.
    /// </summary>
    /// <param name="wordOffset">Смещение слова в <paramref name="sentence"/> или -1.</param>
    IReadOnlyList<DictionaryMatch> Lookup(string word, string? sentence, int wordOffset);
}

/// <summary>
/// Поиск в data/dict.db. Слово: точная форма, таблица словоформ и нижний регистр (кандидаты сравниваются
/// по частотности, наличию перевода и подсказке части речи из контекста), затем правила лемматизации.
/// Фразы: окна из 2–3 слов предложения, содержащие слово под курсором; первое слово окна — в любой форме.
/// Значения каждой статьи ранжирует <see cref="SenseRanker"/>.
/// </summary>
public sealed partial class DictionaryService : IDictionaryService, IDisposable
{
    private const int MaxPhraseWords = 3;
    private const int MaxPhrases = 2;

    private readonly ILogger<DictionaryService> _log;
    private readonly SqliteConnection? _db;
    private readonly Lock _lock = new();

    public bool IsAvailable => _db is not null;

    public DictionaryService(ILogger<DictionaryService> log) : this(Path.Combine(AppPaths.DataDir, "dict.db"), log)
    {
    }

    public DictionaryService(string dbPath, ILogger<DictionaryService> log)
    {
        _log = log;
        if (!File.Exists(dbPath))
        {
            LogMissing(dbPath);
            return;
        }
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        _db.Open();
    }

    public DictionaryMatch? Lookup(string word) =>
        Lookup(word, null, -1).FirstOrDefault(m => m.Kind == MatchKind.Word);

    public double? Zipf(string word)
    {
        if (_db is null || string.IsNullOrWhiteSpace(word)) return null;
        lock (_lock)
            return FindWord(word, PosHint.None)?.Zipf;
    }

    public IReadOnlyList<DictionaryMatch> Lookup(string word, string? sentence, int wordOffset)
    {
        if (_db is null || string.IsNullOrWhiteSpace(word)) return [];
        var tokens = SentenceTokens.Tokenize(sentence);
        int index = SentenceTokens.IndexOf(tokens, word, wordOffset);
        var hint = index >= 0 ? ContextHints.Guess(tokens, index) : PosHint.None;

        var result = new List<DictionaryMatch>();
        lock (_lock)
        {
            var phrases = index >= 0 ? FindPhrases(tokens, index) : [];
            // Идиоматичные фразы — перед словом; сочетания служебных слов и формулы с местоимениями
            // («up and», «see you») — после него и только если идиоматичных нет.
            var idiomatic = phrases.Where(p => IsIdiomatic(p.Candidate.Lemma)).Take(MaxPhrases).ToList();
            var minor = idiomatic.Count == 0 ? phrases.Where(p => !IsIdiomatic(p.Candidate.Lemma)).Take(1).ToList() : [];

            foreach (var phrase in idiomatic)
                result.Add(LoadPhrase(phrase, tokens));
            if (FindWord(word, hint) is { } best)
            {
                var entry = LoadEntry(best.Id, best.Lemma, tokens, index, 1, hint);
                result.Add(new DictionaryMatch(best.Lemma, entry));
            }
            foreach (var phrase in minor)
                result.Add(LoadPhrase(phrase, tokens));
        }
        return result;
    }

    private DictionaryMatch LoadPhrase(PhraseMatch phrase, IReadOnlyList<Token> tokens)
    {
        var entry = LoadEntry(phrase.Candidate.Id, phrase.Candidate.Lemma, tokens, phrase.Start, phrase.Length, PosHint.None);
        return new DictionaryMatch(phrase.Candidate.Lemma, entry, MatchKind.Phrase);
    }

    /// <summary>
    /// Есть знаменательное слово и нет личных местоимений: «give up», «look forward to», «river bank».
    /// Частотность wordfreq у сочетаний служебных слов («up and», «at it») высокая, но смысла в них нет.
    /// </summary>
    internal static bool IsIdiomatic(string phrase)
    {
        var words = phrase.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => !FunctionWords.IsFunction(w)) && !words.Any(FunctionWords.Pronouns.Contains);
    }

    // ------------------------------------------------------------------ слово

    private Candidate? FindWord(string word, PosHint hint)
    {
        string lc = word.ToLowerInvariant();
        // Точная форма, лемма в нижнем регистре и таблица словоформ — одним запросом, лучший по оценке.
        var candidates = QueryWord(word, lc, rank: 0);
        if (candidates.Count == 0)
        {
            // Правила: кандидаты по убыванию правдоподобия, ранние предпочтительнее при прочих равных.
            var rules = Lemmatizer.Candidates(lc);
            for (int i = 0; i < rules.Count; i++)
                candidates.AddRange(QueryWord(rules[i], rules[i], rank: i + 1));
        }
        return candidates.Count == 0 ? null : candidates.MaxBy(c => Score(c, hint));
    }

    private List<Candidate> QueryWord(string word, string lc, int rank)
    {
        var list = new List<Candidate>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = $"""
            SELECT l.id, l.lemma, l.zipf, l.lemma = $word AS exact, 0 AS via_form, {CandidateColumns}
            FROM lemmas l WHERE l.lemma_lc = $lc
            UNION ALL
            SELECT l.id, l.lemma, l.zipf, 0, 1, {CandidateColumns}
            FROM forms f JOIN lemmas l ON l.id = f.lemma_id WHERE f.form_lc = $lc AND l.word_count = 1
            """;
        cmd.Parameters.AddWithValue("$word", word);
        cmd.Parameters.AddWithValue("$lc", lc);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadCandidate(r, rank));
        return list;
    }

    private const string CandidateColumns = """
        EXISTS (SELECT 1 FROM senses s WHERE s.lemma_id = l.id AND s.translations_ru IS NOT NULL) AS has_ru,
        (SELECT group_concat(DISTINCT s.pos) FROM senses s WHERE s.lemma_id = l.id) AS pos_list
        """;

    private static Candidate ReadCandidate(SqliteDataReader r, int rank) =>
        new(r.GetInt64(0), r.GetString(1), r.GetDouble(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5),
            r.IsDBNull(6) ? [] : r.GetString(6).Split(','), rank);

    /// <summary>
    /// Частотность + бонус за русский перевод, точное совпадение регистра и подходящую часть речи.
    /// Так «ran» -> «run» (у существительного «ran» нет перевода), «better» остаётся «better», а не «good»,
    /// «was running» -> «run» (глагол), но «the running» -> «running».
    /// </summary>
    private static double Score(Candidate c, PosHint hint) =>
        c.Zipf + (c.HasRu ? 1.5 : 0) + (c.Exact ? 0.5 : 0) + (c.ViaForm ? 0 : 0.1)
        + (c.PartsOfSpeech.Any(p => ContextHints.Matches(hint, p)) ? (hint == PosHint.Verb ? 2.0 : 1.0) : 0)
        - c.RuleRank * 0.75;

    // ------------------------------------------------------------------ фразы

    private sealed record PhraseMatch(Candidate Candidate, int Start, int Length);

    /// <summary>
    /// Окна из 2–3 слов, содержащие слово под курсором и не пересекающие пунктуацию. Первое слово окна
    /// подставляется и в начальной форме («gave up» -> «give up», «looking forward to» -> «look forward to»).
    /// Порядок: длиннее, затем частотнее.
    /// </summary>
    private List<PhraseMatch> FindPhrases(IReadOnlyList<Token> tokens, int index)
    {
        var found = new List<PhraseMatch>();
        for (int length = MaxPhraseWords; length >= 2; length--)
        {
            for (int start = Math.Max(0, index - length + 1); start <= index && start + length <= tokens.Count; start++)
            {
                if (HasBreak(tokens, start, length)) continue;
                string rest = string.Join(' ', Enumerable.Range(start + 1, length - 1).Select(i => tokens[i].Lower));
                string first = tokens[start].Lower;
                string surface = first + " " + rest;

                var variants = new List<string> { surface };
                foreach (string baseForm in BaseForms(first))
                    variants.Add(baseForm + " " + rest);

                foreach (var c in QueryPhrase(surface, variants))
                {
                    if (!c.HasRu) continue; // фраза показывается первой — без перевода это обычно мусор («look it»)
                    if (found.Any(f => f.Candidate.Id == c.Id)) continue;
                    found.Add(new PhraseMatch(c, start, length));
                }
            }
        }
        return found
            .OrderByDescending(f => f.Length)
            .ThenByDescending(f => f.Candidate.Zipf)
            .ToList();
    }

    private static bool HasBreak(IReadOnlyList<Token> tokens, int start, int length)
    {
        for (int i = start + 1; i < start + length; i++)
        {
            if (tokens[i].BreakBefore) return true;
        }
        return false;
    }

    /// <summary>Начальные формы первого слова фразы: по таблице словоформ и по правилам.</summary>
    private List<string> BaseForms(string word)
    {
        var forms = new List<string>();
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT l.lemma_lc FROM forms f JOIN lemmas l ON l.id = f.lemma_id
                WHERE f.form_lc = $w AND l.word_count = 1 LIMIT 4
                """;
            cmd.Parameters.AddWithValue("$w", word);
            using var r = cmd.ExecuteReader();
            while (r.Read()) forms.Add(r.GetString(0));
        }
        forms.AddRange(Lemmatizer.Candidates(word).Take(4));
        return forms.Distinct(StringComparer.Ordinal).Where(f => f != word).ToList();
    }

    private List<Candidate> QueryPhrase(string surface, List<string> variants)
    {
        var list = new List<Candidate>();
        using var cmd = _db!.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < variants.Count; i++)
        {
            names.Add("$v" + i);
            cmd.Parameters.AddWithValue("$v" + i, variants[i]);
        }
        cmd.CommandText = $"""
            SELECT l.id, l.lemma, l.zipf, 0, 0, {CandidateColumns}
            FROM lemmas l WHERE l.lemma_lc IN ({string.Join(", ", names)}) AND l.word_count > 1
            UNION
            SELECT l.id, l.lemma, l.zipf, 0, 1, {CandidateColumns}
            FROM forms f JOIN lemmas l ON l.id = f.lemma_id WHERE f.form_lc = $surface AND l.word_count > 1
            """;
        cmd.Parameters.AddWithValue("$surface", surface);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadCandidate(r, 0));
        return list;
    }

    // ------------------------------------------------------------------ статья

    private DictEntry LoadEntry(long id, string lemma, IReadOnlyList<Token> tokens, int skipFrom, int skipCount, PosHint hint)
    {
        string? ipa = null, cefr = null;
        IReadOnlyList<string> synonyms = [];
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "SELECT ipa, cefr, synonyms FROM lemmas WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                ipa = r.IsDBNull(0) ? null : r.GetString(0);
                cefr = r.IsDBNull(1) ? null : r.GetString(1);
                synonyms = ParseList(r.IsDBNull(2) ? null : r.GetString(2));
            }
        }

        var senses = new List<Sense>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT pos, definition_en, translations_ru, examples FROM senses WHERE lemma_id = $id ORDER BY ord";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                senses.Add(new Sense(r.GetString(0), r.GetString(1),
                    ParseList(r.IsDBNull(2) ? null : r.GetString(2)),
                    ParseList(r.IsDBNull(3) ? null : r.GetString(3))));
            }
        }
        var ranked = SenseRanker.Rank(senses, tokens, skipFrom, skipCount, hint);
        return new DictEntry(lemma, ipa, cefr, ranked, synonyms);
    }

    private static IReadOnlyList<string> ParseList(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize(json, DictJsonContext.Default.StringArray) ?? [];

    public void Dispose() => _db?.Dispose();

    private sealed record Candidate(long Id, string Lemma, double Zipf, bool Exact, bool ViaForm, bool HasRu,
        string[] PartsOfSpeech, int RuleRank);

    [LoggerMessage(LogLevel.Error, "Словарь не найден: {Path}")]
    private partial void LogMissing(string path);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class DictJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
