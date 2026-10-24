using System.Text;

namespace HoverTranslate.Core.Storage;

public enum ExportFormat
{
    /// <summary>Поля через «;», в кавычках при необходимости; UTF-8 с BOM (Excel открывает без «кракозябр»).</summary>
    Csv,

    /// <summary>Поля через табуляцию; UTF-8 без BOM (Anki читает как есть).</summary>
    Txt,
}

/// <summary>Экспорт для импорта в Anki: слово; перевод; определение; предложение — без строки заголовков.</summary>
public static class AnkiExporter
{
    public static string Build(IEnumerable<WordRecord> records, ExportFormat format)
    {
        var sb = new StringBuilder();
        foreach (var r in records)
        {
            string[] fields = [r.Lemma, r.Translation ?? "", r.Definition ?? "", r.Sentence ?? ""];
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(format == ExportFormat.Csv ? ';' : '\t');
                sb.Append(format == ExportFormat.Csv ? CsvField(fields[i]) : TxtField(fields[i]));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<WordRecord> records, ExportFormat format) =>
        File.WriteAllText(path, Build(records, format), new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ExportFormat.Csv));

    private static string CsvField(string value)
    {
        value = OneLine(value);
        return value.AsSpan().IndexOfAny(";\"") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string TxtField(string value) => OneLine(value).Replace('\t', ' ');

    /// <summary>Перевод строки внутри поля ломает импорт — заменяется пробелом.</summary>
    private static string OneLine(string value) =>
        value.Contains('\n') || value.Contains('\r')
            ? string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : value;
}
