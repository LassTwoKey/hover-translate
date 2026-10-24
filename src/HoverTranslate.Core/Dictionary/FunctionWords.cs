namespace HoverTranslate.Core.Dictionary;

/// <summary>Служебные слова английского: артикли, местоимения, предлоги, союзы, частицы, вспомогательные глаголы.</summary>
public static class FunctionWords
{
    /// <summary>Личные и притяжательные местоимения: фраза с ними — разговорная формула («see you», «give me»).</summary>
    public static readonly IReadOnlySet<string> Pronouns = new HashSet<string>(StringComparer.Ordinal)
    {
        "i", "me", "my", "mine", "myself", "you", "your", "yours", "yourself", "he", "him", "his", "himself",
        "she", "her", "hers", "herself", "it", "its", "itself", "we", "us", "our", "ours", "ourselves",
        "they", "them", "their", "theirs", "themselves", "someone", "somebody", "something", "one's",
    };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(Pronouns, StringComparer.Ordinal)
    {
        // артикли, указательные, кванторы
        "a", "an", "the", "this", "that", "these", "those", "some", "any", "no", "every", "each", "all", "both",
        "either", "neither", "one", "ones", "other", "another", "such", "much", "many", "more", "most", "few",
        "less", "least", "own", "same", "what", "which", "who", "whom", "whose", "there", "here",
        // вспомогательные и модальные глаголы
        "am", "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "having",
        "do", "does", "did", "doing", "done", "will", "would", "shall", "should", "can", "could", "may",
        "might", "must", "ought", "'s", "'re", "'ve", "'d", "'ll", "'m",
        // предлоги и частицы
        "of", "to", "in", "on", "at", "by", "for", "with", "without", "within", "from", "into", "onto", "upon",
        "up", "down", "out", "off", "over", "under", "about", "above", "below", "after", "before", "behind",
        "through", "across", "along", "against", "between", "among", "around", "toward", "towards", "via",
        "per", "since", "until", "till", "near", "beside", "besides", "beyond", "inside", "outside", "away",
        "back", "like", "unlike", "than", "as",
        // союзы и наречия-связки
        "and", "or", "but", "nor", "so", "yet", "if", "then", "else", "when", "where", "why", "how", "while",
        "because", "though", "although", "unless", "whether", "not", "very", "too", "also", "just", "only",
        "even", "ever", "never", "still", "again", "now", "well", "really", "quite", "rather", "etc",
    };

    public static bool IsFunction(string lower) => All.Contains(lower);
}
