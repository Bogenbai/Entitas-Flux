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
    /// Sees .cs files saved outside Unity (an IDE) the moment they hit the disk, while
    /// Unity may still be in the background — regenerating then means the refresh that
    /// follows compiles the component and its generated code once, instead of compiling the
    /// stale state first. Events arrive on a thread-pool thread; they are only collected
    /// here and handed over, debounced, on the main thread.
    /// </summary>
    [InitializeOnLoad]
    static class EntitasSourceWatcher
    {
        static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
        static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        static FileSystemWatcher? _watcher;
        static long _lastEventTicks;

        static EntitasSourceWatcher()
        {
            if (Application.isBatchMode)
                return;

            EditorApplication.update += Update;
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
                // Not fatal: the asset postprocessor still regenerates, just one compile later.
                Debug.LogWarning($"Entitas: not watching Assets/ for component changes ({exception.Message}).");
                _watcher = null;
            }
        }

        static void Stop()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        static void Record(string fullPath)
        {
            Pending[fullPath] = 0;
            System.Threading.Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
        }

        static void Update()
        {
            if (Pending.IsEmpty)
                return;

            var last = new DateTime(System.Threading.Interlocked.Read(ref _lastEventTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - last < Debounce)
                return;

            var root = EntitasTools.ProjectRoot;
            var paths = Pending.Keys.ToArray();
            foreach (var path in paths)
                Pending.TryRemove(path, out _);

            EntitasCodeGeneration.OnSourcesChanged(
                paths.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')),
                fromWatcher: true);
        }
    }

    /// <summary>
    /// The safety net: whatever Unity imports — a git pull, a file created from Unity, a
    /// save the watcher missed — is offered for regeneration too. Changes the watcher
    /// already regenerated for are recognised and skipped.
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

            if (scripts.Length > 0)
                EntitasCodeGeneration.OnSourcesChanged(scripts, fromWatcher: false);
        }
    }
}
