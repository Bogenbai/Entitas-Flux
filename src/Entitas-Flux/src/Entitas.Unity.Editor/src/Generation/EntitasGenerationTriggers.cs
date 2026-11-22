#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Entitas.Unity.Editor.Generation
{
    /// <summary>
    /// Sees .cs files saved outside Unity (an IDE) the moment they hit the disk — ~20 ms
    /// after the write, well before the refresh the IDE asks Unity for (Rider waits ~500 ms).
    /// That head start is what lets generation lock the asset database before the refresh
    /// arrives. Events come in on a thread-pool thread and are only collected here;
    /// <see cref="EntitasCodeGeneration"/> takes them on its next tick, which runs first in
    /// the editor's update chain.
    /// </summary>
    [InitializeOnLoad]
    static class EntitasSourceWatcher
    {
        static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        static FileSystemWatcher? _watcher;

        static EntitasSourceWatcher()
        {
            if (Application.isBatchMode)
                return;

            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            Start();
        }

        static void Start()
        {
            try
            {
                _watcher = new FileSystemWatcher(Application.dataPath, "*.cs")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                _watcher.Changed += (_, e) => Record(e.FullPath);
                _watcher.Created += (_, e) => Record(e.FullPath);
                _watcher.Deleted += (_, e) => Record(e.FullPath);
                _watcher.Renamed += (_, e) =>
                {
                    Record(e.OldFullPath);
                    Record(e.FullPath);
                };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception)
            {
                // Not fatal: the asset postprocessor still regenerates, one compile later.
                Debug.LogWarning($"Entitas: not watching Assets/ for component changes ({exception.Message}).");
                _watcher = null;
            }
        }

        static void Stop()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        static void Record(string fullPath) => Pending[fullPath] = 0;

        /// <summary>Project-relative paths saved since the last call.</summary>
        internal static string[] TakePending()
        {
            if (Pending.IsEmpty)
                return Array.Empty<string>();

            var root = EntitasTools.ProjectRoot;
            var paths = Pending.Keys.ToArray();
            foreach (var path in paths)
                Pending.TryRemove(path, out _);

            return paths.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        }
    }

    /// <summary>
    /// The safety net: whatever Unity imports without the watcher having seen it first — a
    /// file created from Unity, a save the watcher missed — is offered for regeneration too.
    /// For scripts this runs after Unity compiled them, so a run from here is followed by a
    /// refresh of its own (the second compilation). Changes the watcher already regenerated
    /// for are recognised and skipped.
    /// </summary>
    sealed class EntitasGenerationPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (Application.isBatchMode)
                return;

            var scripts = imported.Concat(deleted).Concat(moved).Concat(movedFrom)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Not from inside the refresh: the asset database is locked on the next tick.
            if (scripts.Length > 0)
                EntitasCodeGeneration.QueueImported(scripts);
        }
    }
}
