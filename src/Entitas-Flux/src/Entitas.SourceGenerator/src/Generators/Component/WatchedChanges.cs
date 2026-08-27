using System;
using System.Linq;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Fills in (or removes) the marker assignment a [Watched] component needs in its
    /// entity API.
    ///
    /// Removing means dropping the whole LINE, not just the placeholder: substituting an
    /// empty string leaves a blank, trailing-whitespace line inside every method of every
    /// non-watched component — which is most of them.
    /// </summary>
    public static class WatchedChanges
    {
        public static string Apply(string template, string placeholder, string assignment, bool watched)
        {
            if (watched)
                return template.Replace(placeholder, assignment);

            return string.Join("\n", template
                .Split('\n')
                .Where(line => line.Trim() != placeholder));
        }
    }
}
