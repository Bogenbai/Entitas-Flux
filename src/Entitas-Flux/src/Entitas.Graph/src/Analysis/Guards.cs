using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Entitas.Graph.Analysis
{
    /// <summary>
    /// One clause a write runs under, in the code's own words: `amount &lt; 0`, `!(amount &lt; 0)`
    /// for an else, `case X` (enum members without their type), the `when:` argument of a helper
    /// taking the write as a lambda. <see cref="Site"/> is the branch point it comes from and
    /// <see cref="Negated"/> the side taken there: the body of an if and its else, or of
    /// `if (c) { …; continue; }` and what follows it, exclude each other however they are worded.
    ///
    /// <see cref="Node"/> is that branch point in the code, kept so a call's constant arguments
    /// can decide the clause later (<see cref="ConstantArguments"/>); <see cref="Residual"/>, for
    /// `case X when c`, is the clause left when the case is known to match: its `when`.
    /// </summary>
    sealed class Clause
    {
        public readonly string Text;
        public readonly string? Site;
        public readonly bool Negated;
        public readonly Microsoft.CodeAnalysis.SyntaxNode? Node;
        public readonly Clause? Residual;

        public Clause(string text, string? site, bool negated, Microsoft.CodeAnalysis.SyntaxNode? node = null, Clause? residual = null)
        {
            Text = text;
            Site = site;
            Negated = negated;
            Node = node;
            Residual = residual;
        }

        /// <summary>Whether this and <paramref name="other"/> cannot both hold: the two sides of one branch point, or one written as the other's negation.</summary>
        public bool Excludes(Clause other) =>
            Site != null && Site == other.Site
                ? Negated != other.Negated
                : Guards.Negations(Text).Contains(other.Text, StringComparer.Ordinal) ||
                  Guards.Negations(other.Text).Contains(Text, StringComparer.Ordinal);
    }

    /// <summary>
    /// The clauses one write runs under, all holding together, outermost first: the system's call,
    /// the calls on the way, then the write's own if. That is how the code reads, and when the
    /// text is cut, what goes is the innermost detail, not the case a reader is looking for.
    /// </summary>
    sealed class Condition
    {
        public readonly IReadOnlyList<Clause> Clauses;

        public Condition(IReadOnlyList<Clause> clauses) => Clauses = clauses;

        /// <summary>For ordering and duplicates: the texts in order.</summary>
        public string Key => string.Join("\u0001", Clauses.Select(c => c.Text));

        /// <summary><paramref name="inner"/> reached under this: these clauses, then those of inner not said already.</summary>
        public Condition Then(Condition inner)
        {
            var clauses = new List<Clause>(Clauses);
            foreach (var clause in inner.Clauses)
                if (!Contains(clause))
                    clauses.Add(clause);
            return clauses.Count == Clauses.Count ? this : new Condition(clauses);
        }

        bool Contains(Clause clause) => Clauses.Any(c => c.Text == clause.Text);

        /// <summary>Every clause of this is one of <paramref name="other"/>'s, so other holding means this holds.</summary>
        public bool IsWithin(Condition other) => Clauses.All(other.Contains);

        /// <summary>
        /// One line of at most about <paramref name="length"/> characters: as written, then cut at
        /// the tail — whole clauses while they fit, the next one cut short when enough of it is
        /// left to recognise, otherwise " &amp;&amp; …".
        /// </summary>
        public string Render(int length)
        {
            string Wrap(string text) => Clauses.Count > 1 && Guards.NeedsParentheses(text) ? "(" + text + ")" : text;

            var parts = Clauses.Select(c => Wrap(c.Text)).ToList();
            var full = string.Join(" && ", parts);
            if (full.Length <= length)
                return full;

            if (parts[0].Length > length)
                return Cut(parts[0], length);
            var line = new StringBuilder(parts[0]);
            for (var i = 1; i < parts.Count; i++)
            {
                // The last clause is kept whole when " && …" would be about as long as it.
                var room = i == parts.Count - 1 ? length + Guards.More.Length : length;
                if (line.Length + 4 + parts[i].Length <= room)
                {
                    line.Append(" && ").Append(parts[i]);
                    continue;
                }

                var left = length - line.Length - 4;
                line.Append(left >= Guards.ShortestCut ? " && " + Cut(parts[i], left) : Guards.More);
                break;
            }
            return line.ToString();

            static string Cut(string text, int room) => text.Substring(0, room - 1).TrimEnd() + "…";
        }
    }

    /// <summary>
    /// Conditions as they travel from sites to edges: null means none (the write happens whenever
    /// its method runs), otherwise the ways it can happen, any of which may be the one that holds.
    /// </summary>
    static class Guards
    {
        /// <summary>How long one condition may be on a line; past it, the tail is cut.</summary>
        public const int Length = 70;

        /// <summary>Appended when the clauses after the ones shown are cut.</summary>
        public const string More = " && …";

        /// <summary>A clause cut shorter than this says nothing; " &amp;&amp; …" says as much.</summary>
        public const int ShortestCut = 12;

        /// <summary>Edges name this many conditions, then count the rest.</summary>
        const int Named = 3;

        public static IReadOnlyList<Condition>? One(Condition? condition) => condition == null ? null : new[] { condition };

        /// <summary><paramref name="inner"/> reached under <paramref name="outer"/>: each way there, then inner's clauses.</summary>
        public static IReadOnlyList<Condition>? And(IReadOnlyList<Condition>? outer, Condition? inner) =>
            outer == null ? One(inner) : inner == null ? outer : outer.Select(o => o.Then(inner)).ToList();

        /// <summary>
        /// Another way to the same place, added to <paramref name="ways"/> unless one already there
        /// covers it (its clauses are all in the new one); ways the new one covers go. True when
        /// anything changed. Keeps the ways a method is reached by few, and the walk finite.
        /// </summary>
        public static bool Join(List<Condition> ways, Condition way)
        {
            if (ways.Any(w => w.IsWithin(way)))
                return false;
            ways.RemoveAll(w => way.IsWithin(w));
            ways.Add(way);
            return true;
        }

        /// <summary>
        /// The ways a write happens, simplified: a way another one covers goes (`a` covers `a &amp;&amp;
        /// b`), and two that differ only in the sides of one branch become what they share (`p
        /// &amp;&amp; a` and `p &amp;&amp; !a` are `p`) — so a write made in an if and in its else is
        /// unconditional, which is null. The order the ways come in changes nothing.
        /// </summary>
        public static IReadOnlyList<Condition>? Reduce(IEnumerable<Condition> ways)
        {
            var list = ways.GroupBy(w => w.Key, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
            if (list.Any(w => w.Clauses.Count == 0))
                return null;

            for (var changed = true; changed;)
            {
                changed = false;
                for (var i = 0; i < list.Count && !changed; i++)
                for (var j = 0; j < list.Count && !changed; j++)
                {
                    if (i == j)
                        continue;
                    if (list[i].IsWithin(list[j]))
                    {
                        list.RemoveAt(j);
                        changed = true;
                    }
                    else if (Merge(list[i], list[j]) is { } merged)
                    {
                        if (merged.Clauses.Count == 0)
                            return null;
                        list[i] = merged;
                        list.RemoveAt(j);
                        changed = true;
                    }
                }
            }

            return list;
        }

        /// <summary>`p &amp;&amp; a` and `p &amp;&amp; !a` → `p`; null unless they differ in exactly one clause each, and those exclude each other.</summary>
        static Condition? Merge(Condition a, Condition b)
        {
            if (a.Clauses.Count != b.Clauses.Count)
                return null;
            var onlyA = a.Clauses.Where(c => b.Clauses.All(x => x.Text != c.Text)).ToList();
            var onlyB = b.Clauses.Where(c => a.Clauses.All(x => x.Text != c.Text)).ToList();
            return onlyA.Count == 1 && onlyB.Count == 1 && onlyA[0].Excludes(onlyB[0])
                ? new Condition(a.Clauses.Where(c => c != onlyA[0]).ToList())
                : null;
        }

        /// <summary>"a or b or c or +2 more", ordinal, each cut to <see cref="Length"/>; null when there is none, or the ways cover every case.</summary>
        public static string? Format(IReadOnlyCollection<Condition>? ways) =>
            ways == null || Reduce(ways) is not { } reduced ? null : Name(reduced.Select(w => w.Render(Length)));

        /// <summary>
        /// The same for conditions already written out — those of the edges on one line of flow
        /// (an AddX in an if and a ReplaceX in its else are two edges). Their " &amp;&amp; " parts
        /// are clauses again, so `p &amp;&amp; a` and `p &amp;&amp; !a` still make `p`; a text naming
        /// several ways (" or ") is taken whole. A text cut short ends in a clause standing for all
        /// of it, so two whose hidden tails may differ never merge, while `p` still covers `p
        /// &amp;&amp; …`. Not cut again: each was cut when it was written.
        /// </summary>
        public static string? Format(IReadOnlyCollection<string> texts)
        {
            if (texts.Any(t => Negations(t).Any(texts.Contains)))
                return null;
            var written = new Dictionary<Condition, string>();
            foreach (var text in texts)
            {
                var parts = text.Contains(" or ") ? new List<string> { text } : SplitTopLevel(text, " && ");
                if (parts[parts.Count - 1].EndsWith("…", StringComparison.Ordinal))
                    parts[parts.Count - 1] = text;
                written[new Condition(parts.Select(p => new Clause(p, null, false)).ToList())] = text;
            }

            return Reduce(written.Keys) is { } reduced
                ? Name(reduced.Select(w => written.TryGetValue(w, out var text) ? text : w.Render(int.MaxValue)))
                : null;
        }

        static string Name(IEnumerable<string> ways)
        {
            var texts = ways.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
            return string.Join(" or ", texts.Take(Named)) + (texts.Count > Named ? $" or +{texts.Count - Named} more" : string.Empty);
        }

        /// <summary>
        /// How TreeWalker writes the negation of a condition it wrote as <paramref name="text"/>:
        /// `!x`, `!(a &lt; b)`, `a != b` for `a == b`. Only whole conditions: one cut short has no
        /// exact negation.
        /// </summary>
        public static IEnumerable<string> Negations(string text)
        {
            if (text.EndsWith("…", StringComparison.Ordinal))
                yield break;
            yield return "!" + text;
            yield return $"!({text})";
            if (text.IndexOf(" == ", StringComparison.Ordinal) is var eq && eq > 0 && text.IndexOf(" == ", eq + 1, StringComparison.Ordinal) < 0 && !text.Contains("&&") && !text.Contains("||"))
                yield return text.Substring(0, eq) + " != " + text.Substring(eq + 4);
            if (text.IndexOf(" != ", StringComparison.Ordinal) is var ne && ne > 0 && text.IndexOf(" != ", ne + 1, StringComparison.Ordinal) < 0 && !text.Contains("&&") && !text.Contains("||"))
                yield return text.Substring(0, ne) + " == " + text.Substring(ne + 4);
        }

        /// <summary>Whether a clause joined to others with &amp;&amp; needs parentheses to keep its meaning: an ||, ?? or ?: outside any it has.</summary>
        public static bool NeedsParentheses(string text) =>
            IndexOfTopLevel(text, " || ", 0) >= 0 || IndexOfTopLevel(text, " ?? ", 0) >= 0 || IndexOfTopLevel(text, " ? ", 0) >= 0;

        static List<string> SplitTopLevel(string text, string separator)
        {
            var parts = new List<string>();
            var start = 0;
            for (var at = IndexOfTopLevel(text, separator, 0); at >= 0; at = IndexOfTopLevel(text, separator, start))
            {
                parts.Add(text.Substring(start, at - start));
                start = at + separator.Length;
            }
            parts.Add(text.Substring(start));
            return parts;
        }

        /// <summary>Where <paramref name="token"/> occurs outside brackets and string literals, from <paramref name="from"/>; -1 when it does not.</summary>
        static int IndexOfTopLevel(string text, string token, int from)
        {
            var depth = 0;
            char? quote = null;
            for (var i = from; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != null)
                {
                    if (c == '\\') i++;
                    else if (c == quote) quote = null;
                    continue;
                }

                switch (c)
                {
                    case '"' or '\'':
                        quote = c;
                        continue;
                    case '(' or '[' or '{':
                        depth++;
                        continue;
                    case ')' or ']' or '}':
                        depth--;
                        continue;
                }

                if (depth == 0 && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
                    return i;
            }

            return -1;
        }
    }
}
