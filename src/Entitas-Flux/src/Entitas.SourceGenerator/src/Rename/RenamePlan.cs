using System.Collections.Generic;
using Microsoft.CodeAnalysis.Text;

namespace Entitas.SourceGenerator.Rename
{
    public sealed class RenameOptions
    {
        /// <summary>Also rewrite occurrences inside string literals and comments.</summary>
        public bool IncludeStrings { get; set; }
    }

    /// <summary>One identifier occurrence to rewrite, with the info needed to print it.</summary>
    public sealed class TextEdit
    {
        public TextSpan Span { get; }
        public string OldText { get; }
        public string NewText { get; }
        public int Line { get; }

        public TextEdit(TextSpan span, string oldText, string newText, int line)
        {
            Span = span;
            OldText = oldText;
            NewText = newText;
            Line = line;
        }
    }

    /// <summary>Edits found in an assembly other than the one declaring the component.</summary>
    public sealed class ExternalEdits
    {
        public string AssemblyName { get; }
        public IReadOnlyList<FileEdits> Files { get; }
        public IReadOnlyList<string> Warnings { get; }

        public ExternalEdits(string assemblyName, IReadOnlyList<FileEdits> files, IReadOnlyList<string> warnings)
        {
            AssemblyName = assemblyName;
            Files = files;
            Warnings = warnings;
        }

        public int EditCount
        {
            get
            {
                var count = 0;
                foreach (var file in Files)
                    count += file.Edits.Count;
                return count;
            }
        }
    }

    public sealed class FileEdits
    {
        public string Path { get; }
        public IReadOnlyList<TextEdit> Edits { get; }

        public FileEdits(string path, IReadOnlyList<TextEdit> edits)
        {
            Path = path;
            Edits = edits;
        }
    }

    /// <summary>
    /// The full result of planning a rename: which identifiers change, which files
    /// they occur in, and anything the engine could not decide on its own.
    /// </summary>
    public sealed class RenamePlan
    {
        public string OldFullTypeName { get; set; } = string.Empty;
        public string OldClassName { get; set; } = string.Empty;
        public string NewClassName { get; set; } = string.Empty;
        public string[] ContextNames { get; set; } = new string[0];

        /// <summary>Path of the file declaring the component (null for partials in several files).</summary>
        public string? DeclarationFile { get; set; }

        /// <summary>old identifier -> new identifier, derived from the regenerated output.</summary>
        public IReadOnlyDictionary<string, string> NameMap { get; set; } =
            new Dictionary<string, string>();

        public IReadOnlyList<FileEdits> Files { get; set; } = new FileEdits[0];

        /// <summary>Things a human should look at: unmapped members, skipped ambiguous usages.</summary>
        public IReadOnlyList<string> Warnings { get; set; } = new string[0];

        /// <summary>Assembly that declares the component — the only one the plan's edits cover.</summary>
        public string DeclaringAssemblyName { get; set; } = string.Empty;

        /// <summary>
        /// Every type the generator declares (GameEntity, GameMatcher, GameComponentsLookup,
        /// generated components, listener interfaces …). Used to recognise usages of this
        /// component's generated API in OTHER assemblies, where the generated code is not
        /// visible as source.
        /// </summary>
        public IReadOnlyCollection<string> GeneratedTypeNames { get; set; } = new string[0];

        public int EditCount
        {
            get
            {
                var count = 0;
                foreach (var file in Files)
                    count += file.Edits.Count;
                return count;
            }
        }
    }
}
