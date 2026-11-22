#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Entitas.Unity.Editor.Generation
{
    /// <summary>What the scheduler asks the editor to do, in order.</summary>
    enum GenerationCommand
    {
        /// <summary><c>AssetDatabase.StartAssetEditing()</c>: refreshes requested from now on wait.</summary>
        LockAssetDatabase,

        /// <summary>Start entitas-gen in the background over <see cref="GenerationScheduler.RunPaths"/>.</summary>
        StartRun,

        /// <summary><c>AssetDatabase.StopAssetEditing()</c>: a refresh that waited runs now, with the generated files.</summary>
        UnlockAssetDatabase,

        /// <summary><c>AssetDatabase.Refresh()</c>: a compilation already used stale files, so import the new ones.</summary>
        Refresh
    }

    /// <summary>
    /// Decides WHEN automatic generation runs relative to Unity's own refresh and compile.
    /// Pure logic with an injected clock — no Unity API — so the ordering is unit-tested.
    ///
    /// The problem it solves: a component saved from the IDE makes Unity refresh and compile
    /// (Rider asks for that ~500 ms after a save; focusing Unity does it too). Generation run
    /// alongside that compile is too late: a new component's generated file is not part of
    /// it (its API arrives one compile later), and a generated file deleted mid-compile fails
    /// it (CS2001). Unity offers no managed hook between noticing a changed script and
    /// compiling it — AssetPostprocessor callbacks for scripts run after the compile, and
    /// CompilationPipeline.compilationStarted does not hold the compiler back.
    ///
    /// So generation must win the race instead: the moment the file watcher reports a
    /// relevant save, the asset database is locked (StartAssetEditing), which makes any
    /// refresh requested meanwhile wait. Generation runs, then the lock is released and the
    /// refresh that waited imports the component and its generated files together: one
    /// compilation, already complete.
    ///
    /// When the race is lost anyway — a compilation started before the lock — generation
    /// waits for it to finish (deleting files under a running compile is what produces
    /// CS2001), and asks for a refresh afterwards, so the second compilation happens by
    /// itself. The same applies to changes that only reach us after Unity imported them.
    /// </summary>
    sealed class GenerationScheduler
    {
        public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(150);

        readonly TimeSpan _quietPeriod;
        readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime _lastChange;
        bool _locked;
        bool _running;
        bool _forced;
        bool _refreshAfter;
        bool _changedWhileLocked;

        public GenerationScheduler(TimeSpan? quietPeriod = null)
        {
            _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        }

        public bool IsLocked => _locked;
        public bool IsRunning => _running;

        /// <summary>Nothing pending, running or locked.</summary>
        public bool IsIdle => !_locked && !_running && !_forced && _pending.Count == 0;

        /// <summary>The changed paths the run just started for (after <see cref="GenerationCommand.StartRun"/>).</summary>
        public IReadOnlyCollection<string> RunPaths { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Relevant source files changed. <paramref name="alreadyCompiled"/>: Unity imported and
        /// compiled them before we could lock (the asset postprocessor path, or a compilation
        /// that started after the save) — that compilation used stale generated files.
        /// </summary>
        public List<GenerationCommand> SourcesChanged(IEnumerable<string> paths, DateTime now, bool alreadyCompiled)
        {
            var commands = new List<GenerationCommand>();
            var added = false;
            foreach (var path in paths)
                added |= _pending.Add(path);

            if (!added)
                return commands;

            _lastChange = now;
            _refreshAfter |= alreadyCompiled;
            Lock(commands);
            return commands;
        }

        /// <summary>
        /// An explicit run (Tools > Entitas > Generate) that could not run at once because
        /// Unity was compiling or a run was already going; it always imports what it changes.
        /// </summary>
        public List<GenerationCommand> RunRequested(DateTime now)
        {
            var commands = new List<GenerationCommand>();
            _forced = true;
            _refreshAfter = true;
            _lastChange = now - _quietPeriod;
            Lock(commands);
            return commands;
        }

        /// <summary>A compilation started: if we were holding changes back, it compiled without them.</summary>
        public void CompilationStarted()
        {
            if (_locked || _running || _pending.Count > 0)
                _refreshAfter = true;
        }

        /// <summary>Called every editor tick.</summary>
        public List<GenerationCommand> Tick(DateTime now, bool isCompiling)
        {
            var commands = new List<GenerationCommand>();
            if (_running)
                return commands;

            if (_pending.Count == 0 && !_forced)
            {
                // Not reachable through the other inputs; never leave the database locked.
                if (_locked)
                    Unlock(commands);
                return commands;
            }

            // Let a burst of saves (Save All, a multi-file refactoring) settle into one run.
            if (now - _lastChange < _quietPeriod)
                return commands;

            if (isCompiling)
            {
                // Started before the lock, so it uses the stale files. Never write or delete
                // under a running compile; go after it and refresh once done.
                _refreshAfter = true;
                return commands;
            }

            RunPaths = _pending.ToArray();
            _pending.Clear();
            _forced = false;
            _running = true;
            commands.Add(GenerationCommand.StartRun);
            return commands;
        }

        /// <summary>The run ended; <paramref name="changedFiles"/>: it wrote or deleted something.</summary>
        public List<GenerationCommand> RunFinished(bool changedFiles)
        {
            var commands = new List<GenerationCommand>();
            _running = false;
            _changedWhileLocked |= changedFiles;

            // More saves arrived meanwhile: stay locked, the next tick runs again.
            if (_pending.Count > 0 || _forced)
                return commands;

            var refresh = _changedWhileLocked && _refreshAfter;
            Unlock(commands);
            if (refresh)
                commands.Add(GenerationCommand.Refresh);

            return commands;
        }

        /// <summary>Domain reload, play mode, quitting: forget everything and never stay locked.</summary>
        public List<GenerationCommand> Reset()
        {
            var commands = new List<GenerationCommand>();
            _pending.Clear();
            _forced = false;
            _running = false;
            if (_locked)
                Unlock(commands);
            return commands;
        }

        void Lock(List<GenerationCommand> commands)
        {
            if (_locked)
                return;

            _locked = true;
            commands.Add(GenerationCommand.LockAssetDatabase);
        }

        void Unlock(List<GenerationCommand> commands)
        {
            if (!_locked)
                return;

            _locked = false;
            _refreshAfter = false;
            _changedWhileLocked = false;
            commands.Add(GenerationCommand.UnlockAssetDatabase);
        }
    }
}
