using System;
using System.Collections.Generic;

namespace Entitas.Graph
{
    /// <summary>Where one side of a diff comes from.</summary>
    public enum DiffSource
    {
        /// <summary>A saved graph.json.</summary>
        GraphFile,

        /// <summary>The project's sources at a git ref, taken with <c>git archive</c>.</summary>
        GitRef,

        /// <summary>The project as it is on disk (graph.json, rebuilt when stale).</summary>
        WorkingTree
    }

    /// <summary>One side of a diff: where it comes from and how the header names it.</summary>
    public sealed class DiffSide
    {
        public static readonly DiffSide WorkingTree = new DiffSide(DiffSource.WorkingTree, string.Empty, "working tree");

        public DiffSource Source { get; }

        /// <summary>The graph file's path or the git ref to build from; empty for the working tree.</summary>
        public string Value { get; }

        public string Label { get; }

        public DiffSide(DiffSource source, string value, string label)
        {
            Source = source;
            Value = value;
            Label = label;
        }

        public static DiffSide File(string path) => new DiffSide(DiffSource.GraphFile, path, path);

        public static DiffSide Ref(string gitRef) => new DiffSide(DiffSource.GitRef, gitRef, gitRef);
    }

    /// <summary>A diff request that cannot be carried out as given; the command line prints usage with it.</summary>
    public sealed class DiffUsageException : Exception
    {
        public DiffUsageException(string message) : base(message) { }
    }

    /// <summary>
    /// What <c>diff</c> compares, decided from its arguments alone so every combination is
    /// tested without git:
    ///
    /// - <c>diff old.json [new.json]</c>: two saved graphs, or one and the working tree;
    /// - <c>diff --ref A</c>: the project at A against the working tree;
    /// - <c>diff --ref A --ref B</c>: the project at A against the project at B;
    /// - <c>diff --commit X</c>: X's first parent against X — "what did X change".
    ///
    /// Two refs exist because a ref against the working tree answers "what did X change" only
    /// while nothing after X touched the same code; X^ against X answers it at any later time.
    /// </summary>
    public sealed class DiffRequest
    {
        public const int MaxRefs = 2;

        public DiffSide Old { get; }
        public DiffSide New { get; }

        /// <summary>The commit named by <c>--commit</c>; null for the other forms.</summary>
        public string? Commit { get; }

        /// <summary>A header line after old/new that says how the sides were chosen, when that is not obvious.</summary>
        public string? Note { get; }

        DiffRequest(DiffSide old, DiffSide @new, string? commit = null, string? note = null)
        {
            Old = old;
            New = @new;
            Commit = commit;
            Note = note;
        }

        public static DiffRequest From(IReadOnlyList<string> refs, string? commit, IReadOnlyList<string> graphFiles)
        {
            if (commit != null)
            {
                if (refs.Count > 0)
                    throw new DiffUsageException($"--commit {commit} already names both sides ({commit}^ and {commit}); use it or --ref, not both");
                if (graphFiles.Count > 0)
                    throw new DiffUsageException("diff takes --commit <commit> or graph files, not both");
                // The old side is resolved to the first parent once git has been asked (WithParents).
                return new DiffRequest(DiffSide.Ref(commit + "^"), DiffSide.Ref(commit), commit);
            }

            if (refs.Count > MaxRefs)
                throw new DiffUsageException($"diff takes at most {MaxRefs} --ref (old, then new), not {refs.Count}");
            if (refs.Count > 0 && graphFiles.Count > 0)
                throw new DiffUsageException("diff takes --ref <git-ref> or graph files, not both");
            if (refs.Count == 2)
                return new DiffRequest(DiffSide.Ref(refs[0]), DiffSide.Ref(refs[1]));
            if (refs.Count == 1)
                return new DiffRequest(DiffSide.Ref(refs[0]), DiffSide.WorkingTree);

            if (graphFiles.Count == 0)
                throw new DiffUsageException("'diff' needs a graph file, --ref <git-ref> or --commit <commit>");
            if (graphFiles.Count > 2)
                throw new DiffUsageException("diff takes at most two graph files (old, then new)");
            return new DiffRequest(DiffSide.File(graphFiles[0]), graphFiles.Count > 1 ? DiffSide.File(graphFiles[1]) : DiffSide.WorkingTree);
        }

        /// <summary>
        /// <c>--commit</c> once git has listed the commit's parents (<see cref="RefSnapshot.Parents"/>):
        /// the old side is built from the first parent's id, so it is exactly the commit the
        /// header names. A merge is compared with its first parent — the branch it was merged
        /// into, as <c>git log --first-parent</c> sees it — and the header says so, because the
        /// diff then holds the whole merged branch. A root commit has nothing to compare with.
        /// </summary>
        public DiffRequest WithParents(IReadOnlyList<string> parents)
        {
            if (Commit == null)
                throw new InvalidOperationException("only a --commit request has parents to resolve");
            if (parents.Count == 0)
                throw new InvalidOperationException(
                    $"{Commit} is a root commit: it has no parent to compare it with. 'diff --ref {Commit}' compares it with the working tree.");

            var note = parents.Count > 1
                ? $"{Commit} is a merge: old is its first parent, so this is what the merge brought into that branch."
                : null;
            return new DiffRequest(new DiffSide(DiffSource.GitRef, parents[0], Old.Label), New, Commit, note);
        }
    }
}
