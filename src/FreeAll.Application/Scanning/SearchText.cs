using System.Text;

namespace FreeAll.Application.Scanning;

/// <summary>
/// Turns what the user typed into a full-text query (F-017). Each run of letters and digits becomes a
/// quoted prefix term, so "annual rep" finds "Annual report 2024.pdf" and punctuation can never form
/// query syntax. Every word must match. Case and accents are ignored by the index's tokenizer.
/// </summary>
public static class SearchText
{
    public const int MaxWords = 8;

    /// <returns>The query, or null when there is nothing specific enough to search for (no words, or a single one-character word).</returns>
    public static string? ToMatchExpression(string? text)
    {
        var words = Words(text);
        if (words.Count == 0 || (words.Count == 1 && words[0].Length < 2))
        {
            return null;
        }

        return string.Join(" AND ", words.Select(w => "\"" + w + "\"*"));
    }

    /// <summary>The words of <paramref name="text"/>, lower-cased, at most <see cref="MaxWords"/>.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var current = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(Rune.ToLowerInvariant(rune).ToString());
                continue;
            }

            Flush();
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0 && words.Count < MaxWords)
            {
                words.Add(current.ToString());
            }

            current.Clear();
        }
    }

    /// <summary>For display: the words actually searched for.</summary>
    public static string Describe(string? text) => string.Join(" ", Words(text));
}
