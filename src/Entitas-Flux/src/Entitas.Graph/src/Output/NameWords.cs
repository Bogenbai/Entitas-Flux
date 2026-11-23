using System;
using System.Collections.Generic;
using System.Linq;

namespace Entitas.Graph.Output
{
    /// <summary>
    /// How `query` compares its words with a name: word by word, not as text. Matched as text,
    /// "cat" found every Indicator, Notification and Application on a 2000-file project, and
    /// "placement" missed PlaceFurnitureByRequestSystem, the system that does the placing. So names are split
    /// at their camel-case words and both sides are reduced to a rough stem — plural, -ment,
    /// -ing, -ed and a final e go — which makes place, places, placed, placing and placement
    /// one word. It is deliberately crude: identifiers are short, and a stem that joins two
    /// words now and then costs a seed, while one that misses costs the answer.
    /// </summary>
    internal static class NameWords
    {
        /// <summary>
        /// Words that carry no topic, in a question ("how does placement work") or in a name
        /// (SpawnOnRequest, OrderIsPaidEvent). As query words they would match every XOnY system.
        /// </summary>
        static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "by", "for", "from", "with", "into",
            "is", "are", "was", "be", "do", "does", "did", "how", "what", "who", "which", "where", "when", "why",
            "it", "its", "this", "that"
        };

        public static bool IsStopWord(string word) => StopWords.Contains(word);

        /// <summary>
        /// "PlaceFurnitureByRequestSystem" → place, furniture, by, request, system; "UIRoot" →
        /// ui, root; "Vector3Value" → vector, 3, value. Anything but a letter or digit separates.
        /// </summary>
        public static List<string> Split(string name)
        {
            var words = new List<string>();
            var start = -1;
            for (var i = 0; i <= name.Length; i++)
            {
                var separator = i == name.Length || !char.IsLetterOrDigit(name[i]);
                if (start >= 0 && (separator || Boundary(name, i)))
                {
                    words.Add(name.Substring(start, i - start).ToLowerInvariant());
                    start = -1;
                }

                if (!separator && start < 0)
                    start = i;
            }

            return words;
        }

        /// <summary>A word starts at i: aB, a1, 1a, or the last capital of an acronym (UIRoot → UI|Root).</summary>
        static bool Boundary(string s, int i)
        {
            char previous = s[i - 1], current = s[i];
            if (!char.IsLetterOrDigit(previous))
                return false;
            if (char.IsDigit(previous) != char.IsDigit(current))
                return true;
            if (char.IsLower(previous) && char.IsUpper(current))
                return true;
            return char.IsUpper(previous) && char.IsUpper(current) && i + 1 < s.Length && char.IsLower(s[i + 1]);
        }

        /// <summary>
        /// A lower-case word's rough stem: placement, placed, placing, places → plac; petting,
        /// petted, pets → pet; achievements → achiev. A suffix goes only when what is left is three
        /// letters or more with a vowel in it, so string and thing stay whole.
        /// </summary>
        public static string Stem(string word)
        {
            var w = word;

            if (w.EndsWith("sses", StringComparison.Ordinal))
                w = w.Substring(0, w.Length - 2);
            else if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4)
                w = w.Substring(0, w.Length - 3) + "y";
            else if (w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal) &&
                     !w.EndsWith("us", StringComparison.Ordinal) && !w.EndsWith("is", StringComparison.Ordinal) && Stemmable(w, 1))
                w = w.Substring(0, w.Length - 1);

            if (w.EndsWith("ment", StringComparison.Ordinal) && Stemmable(w, 4))
                w = w.Substring(0, w.Length - 4);
            else if (w.EndsWith("ied", StringComparison.Ordinal) && w.Length > 4)
                w = w.Substring(0, w.Length - 3) + "y";
            else if (w.EndsWith("ing", StringComparison.Ordinal) && Stemmable(w, 3))
                w = Undouble(w.Substring(0, w.Length - 3));
            // speed, need and seed are not past tenses.
            else if (w.EndsWith("ed", StringComparison.Ordinal) && Stemmable(w, 2) && w[w.Length - 3] != 'e')
                w = Undouble(w.Substring(0, w.Length - 2));

            if (w.Length > 3 && w[w.Length - 1] == 'e')
                w = w.Substring(0, w.Length - 1);
            return w;
        }

        static bool Stemmable(string word, int suffix)
        {
            var length = word.Length - suffix;
            if (length < 3)
                return false;
            for (var i = 0; i < length; i++)
                if ("aeiouy".IndexOf(word[i]) >= 0)
                    return true;
            return false;
        }

        /// <summary>petting → pett → pet, committed → committ → commit; but filled, passed and added keep theirs.</summary>
        static string Undouble(string stem)
        {
            var n = stem.Length;
            return n >= 4 && stem[n - 1] == stem[n - 2] && "aeiouylsz".IndexOf(stem[n - 1]) < 0
                ? stem.Substring(0, n - 1)
                : stem;
        }

        /// <summary>How a query word meets a run of name words.</summary>
        public enum Fit
        {
            None = 0,

            /// <summary>The start of a word or of a run of words: "furn", "placefurn". Three letters at least.</summary>
            Prefix = 1,

            /// <summary>A word, or a run of words written as one ("placefurniturerequest"), stem for stem.</summary>
            Whole = 2
        }

        /// <summary>
        /// The best fit of a query word (lower case) and its stem in a name's words, at word
        /// boundaries only. The words it takes up are marked in <paramref name="covered"/>: what
        /// is left unmarked is what the name says besides the query.
        /// </summary>
        public static Fit Match(string term, string stem, IReadOnlyList<string> words, bool[]? covered = null)
        {
            int prefixFrom = -1, prefixTo = -1;
            for (var i = 0; i < words.Count; i++)
            {
                // A stem keeps the first letter, so a run starting with another letter cannot fit;
                // this skips nearly every word of nearly every name, once per node and query word.
                if (words[i][0] != term[0])
                    continue;

                var run = string.Empty;
                var j = i;
                for (; j < words.Count; j++)
                {
                    run += words[j];
                    if (Stem(run) == stem)
                    {
                        Cover(covered, i, j);
                        return Fit.Whole;
                    }

                    if (run.Length >= term.Length)
                        break;
                }

                if (prefixFrom < 0 && term.Length >= 3 && run.StartsWith(term, StringComparison.Ordinal))
                    (prefixFrom, prefixTo) = (i, Math.Min(j, words.Count - 1));
            }

            if (prefixFrom < 0)
                return Fit.None;
            Cover(covered, prefixFrom, prefixTo);
            return Fit.Prefix;
        }

        static void Cover(bool[]? covered, int from, int to)
        {
            if (covered == null)
                return;
            for (var w = from; w <= to; w++)
                covered[w] = true;
        }

        /// <summary>The words of a type's namespace: Code.Gameplay.Features.Placement.Systems.X → code, gameplay, features, placement, systems.</summary>
        public static List<string> NamespaceWords(string? fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return new List<string>();
            var dot = fullName!.LastIndexOf('.');
            return dot < 0 ? new List<string>() : Split(fullName.Substring(0, dot));
        }
    }
}
