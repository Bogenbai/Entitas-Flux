#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Entitas.Unity.Editor.Generation
{
    /// <summary>
    /// Unity side of <c>[assembly: EntitasGeneration(Output = GenerationOutput.Files)]</c>:
    /// asks entitas-gen to bring the generated files up to date — from the menu, from a
    /// batchmode command line, and automatically when a component changes.
    ///
    /// Automatic runs must finish BEFORE Unity compiles the change that caused them, or the
    /// compile misses a new component's generated file (its API arrives one compile late) or
    /// trips over a generated file deleted under it (CS2001). Unity has no managed hook
    /// between noticing a changed script and compiling it, so <see cref="GenerationScheduler"/>
    /// wins that race instead: the file watcher sees an IDE save within milliseconds, the
    /// asset database is locked at once (any refresh requested meanwhile — Rider's comes
    /// ~500 ms after the save — waits), entitas-gen runs, and unlocking lets the refresh that
    /// waited import the component and its generated files together: one compilation. See
    /// the scheduler for the fallback when the race is lost anyway.
    ///
    /// Runs are cheap to skip: only files that declare components or the generation config
    /// (or might, judging by their text) trigger one, and entitas-gen rewrites only the
    /// files whose content actually changed.
    /// </summary>
    [InitializeOnLoad]
    public static class EntitasCodeGeneration
    {
        const string MenuGenerate = "Tools/Entitas/Generate";
        const string MenuAutomatic = "Tools/Entitas/Generate Automatically";
        const string MenuCheck = "Tools/Entitas/Check Generated Files";

        static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        static readonly GenerationSettings Settings = new GenerationSettings(new EditorUserSettingsStore());
        static readonly GenerationScheduler Scheduler = new GenerationScheduler();
        static GenerationState? _state;
        static DateTime _lastCompilationStartedUtc = DateTime.MinValue;

        /// <summary>Kept in SessionState: the postprocessor runs after a domain reload, which clears statics.</summary>
        static DateTime LastRunStartedUtc
        {
            get => new DateTime(long.TryParse(SessionState.GetString(LastRunKey, "0"), out var ticks) ? ticks : 0, DateTimeKind.Utc);
            set => SessionState.SetString(LastRunKey, value.Ticks.ToString());
        }

        const string LastRunKey = "Entitas.Generation.LastRunStartedUtc";

        static EntitasCodeGeneration()
        {
            // First in the update chain: when a save and a refresh request land in the same
            // tick, the lock must be taken before the refresh runs.
            EditorApplication.update -= Tick;
            EditorApplication.update = (EditorApplication.CallbackFunction)Tick + EditorApplication.update;

            CompilationPipeline.compilationStarted += _ =>
            {
                _lastCompilationStartedUtc = DateTime.UtcNow;
                Scheduler.CompilationStarted();
            };

            // Never leave the asset database locked across a domain reload, play mode or exit.
            AssemblyReloadEvents.beforeAssemblyReload += ResetScheduler;
            EditorApplication.quitting += ResetScheduler;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode)
                    ResetScheduler();
            };

            // The checkmark is native menu state: it outlives a domain reload, and the validate
            // function only runs when the menu is drawn, so anything reading it in between got
            // the last drawn value (unchecked in a fresh editor). Setting it once is not enough:
            // menus are not built yet while InitializeOnLoad runs, and after an editor assembly
            // changes (the package DLL replaced) Unity rebuilds them some frames later, which
            // clears the checkmark again — seen on a real project, where one delayCall was too
            // early. So keep it in sync for a few seconds after every load.
            _menuSyncUntil = EditorApplication.timeSinceStartup + MenuSyncSeconds;
            EditorApplication.update -= KeepMenuInSync;
            EditorApplication.update += KeepMenuInSync;
        }

        const double MenuSyncSeconds = 10;
        static double _menuSyncUntil;

        static void KeepMenuInSync()
        {
            if (Menu.GetChecked(MenuAutomatic) != Automatic)
                SyncAutomaticMenu();
            if (EditorApplication.timeSinceStartup >= _menuSyncUntil)
                EditorApplication.update -= KeepMenuInSync;
        }

        static string StatePath => Path.Combine(EntitasTools.ScratchDirectory, "state.json");

        static GenerationState State
        {
            get
            {
                if (_state != null)
                    return _state;

                try
                {
                    _state = File.Exists(StatePath)
                        ? JsonUtility.FromJson<GenerationState>(File.ReadAllText(StatePath))
                        : null;
                }
                catch (Exception)
                {
                    _state = null;
                }

                return _state ??= GuessStateFromCompiledAssemblies();
            }
        }

        /// <summary>
        /// Whether components changing regenerates by itself. On by default. Kept per project
        /// and per user, in UserSettings/EditorUserSettings.asset — not in EditorPrefs, which
        /// every project on the machine shares (see <see cref="GenerationSettings"/>).
        /// </summary>
        public static bool Automatic
        {
            get => Settings.Automatic;
            set
            {
                Settings.Automatic = value;
                SyncAutomaticMenu();
            }
        }

        static void SyncAutomaticMenu() => Menu.SetChecked(MenuAutomatic, Automatic);

        sealed class EditorUserSettingsStore : IProjectSettingsStore
        {
            public string? Get(string key)
            {
                var value = EditorUserSettings.GetConfigValue(key);
                return string.IsNullOrEmpty(value) ? null : value;
            }

            public void Set(string key, string value) => EditorUserSettings.SetConfigValue(key, value);
        }

        // -- menu ---------------------------------------------------------------------

        [MenuItem(MenuGenerate, false, EntitasMenuItemPriorities.generate)]
        public static void GenerateFromMenu()
        {
            // Writing or deleting files under a running compile fails it (CS2001): wait for it.
            if (EditorApplication.isCompiling || !Scheduler.IsIdle)
            {
                Debug.Log("Entitas: generation will run as soon as the current compilation (or generation) finishes.");
                Execute(Scheduler.RunRequested(DateTime.UtcNow));
                return;
            }

            // Synchronous: the main thread is busy, so no refresh can start meanwhile; the
            // refresh afterwards imports everything that changed in one go.
            var response = Generate(check: false, showProgress: true);
            if (response != null)
                Apply(response, refresh: true, logUnchanged: true);
        }

        [MenuItem(MenuCheck, false, EntitasMenuItemPriorities.generate + 1)]
        public static void CheckFromMenu()
        {
            var response = Generate(check: true, showProgress: true);
            if (response != null)
                Apply(response, refresh: false, logUnchanged: true);
        }

        [MenuItem(MenuAutomatic, false, EntitasMenuItemPriorities.generate + 2)]
        static void ToggleAutomatic() => Automatic = !Automatic;

        [MenuItem(MenuAutomatic, true)]
        static bool ToggleAutomaticValidate()
        {
            SyncAutomaticMenu();
            return true;
        }

        // -- batchmode ------------------------------------------------------------------

        /// <summary>
        /// <c>Unity -batchmode -projectPath . -executeMethod
        /// Entitas.Unity.Editor.Generation.EntitasCodeGeneration.GenerateFromCommandLine -quit</c>
        /// — exits 1 when generation failed.
        /// </summary>
        public static void GenerateFromCommandLine() => RunFromCommandLine(check: false);

        /// <summary>Same, writing nothing and exiting 1 when a generated file is out of date — for CI.</summary>
        public static void CheckFromCommandLine() => RunFromCommandLine(check: true);

        static void RunFromCommandLine(bool check)
        {
            var response = Generate(check, showProgress: false);
            var failed = response == null || !Apply(response, refresh: !check, logUnchanged: true) ||
                         (check && response.assemblies.Any(a => a.stale.Length > 0));
            EditorApplication.Exit(failed ? 1 : 0);
        }

        // -- running ----------------------------------------------------------------------

        /// <summary>Runs entitas-gen synchronously; null (and an error in the console) when it could not run.</summary>
        static GenerationResponse? Generate(bool check, bool showProgress)
        {
            var tool = EntitasTools.FindTool(EntitasTools.GeneratorName);
            if (tool == null)
            {
                Debug.LogError(
                    $"Entitas: {EntitasTools.GeneratorName}.dll was not found next to the package's editor assembly (Tools~/). " +
                    "Reinstall the package, or point EditorPrefs '" + EntitasTools.ToolsPathPreference + "' at a folder holding it.");
                return null;
            }

            var (requestPath, responsePath) = WriteRequest(BuildRequest(check, extraSources: null));
            try
            {
                if (showProgress)
                    EditorUtility.DisplayProgressBar("Entitas", check ? "Checking generated files…" : "Generating code…", 0.5f);

                LastRunStartedUtc = DateTime.UtcNow;
                var result = EntitasTools.Run(tool, new[] { "--request", requestPath, "--response", responsePath });
                return ReadResponse(result, responsePath);
            }
            finally
            {
                if (showProgress)
                    EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Called with project-relative paths of .cs files that changed — by the watcher as
        /// soon as they are saved, by the postprocessor after Unity imported (and compiled) them.
        /// </summary>
        internal static void OnSourcesChanged(IEnumerable<string> paths, bool fromWatcher)
        {
            if (!Automatic || EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var relevant = paths.Where(path => IsRelevant(path, fromWatcher)).ToList();
            if (relevant.Count == 0)
                return;

            // Unity already compiled these changes if they came through an import, or if a
            // compilation started after they were written (it beat the watcher to them).
            var alreadyCompiled = !fromWatcher || relevant.Any(path => WrittenBefore(path, _lastCompilationStartedUtc));
            Execute(Scheduler.SourcesChanged(relevant, DateTime.UtcNow, alreadyCompiled));
        }

        static bool WrittenBefore(string path, DateTime moment)
        {
            var full = Path.Combine(EntitasTools.ProjectRoot, path);
            return File.Exists(full) && File.GetLastWriteTimeUtc(full) <= moment;
        }

        /// <summary>Changes Unity imported, handed over from the asset postprocessor.</summary>
        internal static void QueueImported(string[] paths) =>
            MainThread.Enqueue(() => OnSourcesChanged(paths, fromWatcher: false));

        static void Tick()
        {
            var saved = EntitasSourceWatcher.TakePending();
            if (saved.Length > 0)
                OnSourcesChanged(saved, fromWatcher: true);

            DrainMainThreadQueue();
            Execute(Scheduler.Tick(DateTime.UtcNow, EditorApplication.isCompiling));
        }

        static void ResetScheduler() => Execute(Scheduler.Reset());

        static void Execute(List<GenerationCommand> commands)
        {
            foreach (var command in commands)
            {
                switch (command)
                {
                    case GenerationCommand.LockAssetDatabase:
                        AssetDatabase.StartAssetEditing();
                        break;
                    case GenerationCommand.UnlockAssetDatabase:
                        AssetDatabase.StopAssetEditing();
                        break;
                    case GenerationCommand.StartRun:
                        StartBackgroundRun(Scheduler.RunPaths.ToList());
                        break;
                    case GenerationCommand.Refresh:
                        AssetDatabase.Refresh();
                        break;
                }
            }
        }

        static void StartBackgroundRun(List<string> changed)
        {
            var tool = EntitasTools.FindTool(EntitasTools.GeneratorName);
            if (tool == null)
            {
                Debug.LogError($"Entitas: {EntitasTools.GeneratorName}.dll was not found next to the package's editor assembly (Tools~/).");
                Execute(Scheduler.RunFinished(changedFiles: false));
                return;
            }

            // Files the watcher saw may not be imported yet, so Unity's assembly list does
            // not have them: add them to the assembly their folder belongs to.
            var (requestPath, responsePath) = WriteRequest(BuildRequest(check: false, extraSources: changed));
            LastRunStartedUtc = DateTime.UtcNow;

            var stopwatch = Stopwatch.StartNew();
            var process = new Process
            {
                StartInfo = EntitasTools.StartInfo(tool, new[] { "--request", requestPath, "--response", responsePath }),
                EnableRaisingEvents = true
            };
            var error = new System.Text.StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
            process.OutputDataReceived += (_, _) => { };
            process.Exited += (_, _) =>
            {
                var result = new EntitasTools.Result
                {
                    ExitCode = process.ExitCode,
                    Error = error.ToString(),
                    Milliseconds = stopwatch.ElapsedMilliseconds
                };
                process.Dispose();
                MainThread.Enqueue(() => FinishBackgroundRun(result, responsePath));
            };

            try
            {
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
            }
            catch (Exception exception)
            {
                Debug.LogError($"Entitas: could not start {EntitasTools.GeneratorName}: {exception.Message}");
                Execute(Scheduler.RunFinished(changedFiles: false));
            }
        }

        static void FinishBackgroundRun(EntitasTools.Result result, string responsePath)
        {
            var response = ReadResponse(result, responsePath);
            var changed = false;
            if (response != null)
            {
                // No refresh here: unlocking lets the refresh that waited run, and the
                // scheduler adds one only when a compilation already went ahead without us.
                Apply(response, refresh: false, logUnchanged: false);
                changed = response.assemblies.Any(a => a.written.Count > 0 || a.deleted.Count > 0);
            }

            Execute(Scheduler.RunFinished(changed));
        }

        static void DrainMainThreadQueue()
        {
            while (MainThread.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        // -- deciding whether a change matters ---------------------------------------------

        static readonly string[] Signals =
        {
            "IComponent", "ContextDefinition", "EntitasGeneration", "DisableEntitasGenerator", "Entitas.CodeGeneration.Attributes"
        };

        static bool IsRelevant(string path, bool fromWatcher)
        {
            path = path.Replace('\\', '/');
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            var state = State;
            if (state.outputDirectories.Any(directory => path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)))
                return false;

            var full = Path.Combine(EntitasTools.ProjectRoot, path);
            var exists = File.Exists(full);

            // Already accounted for: Unity importing a file the watcher regenerated for.
            if (!fromWatcher && exists && File.GetLastWriteTimeUtc(full) < LastRunStartedUtc)
                return false;

            var known = state.componentFiles.Contains(path, StringComparer.OrdinalIgnoreCase);
            if (!exists)
                return state.filesMode && known;

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (IOException)
            {
                return false; // still being written; the next event will catch it
            }

            if (text.StartsWith("// <auto-generated/>\n// Generated by Entitas-Flux", StringComparison.Ordinal) ||
                text.StartsWith("// <auto-generated/>\r\n// Generated by Entitas-Flux", StringComparison.Ordinal))
                return false;

            // Switching the output mode is relevant whatever the current one is: turning
            // files on writes them, turning them off deletes them.
            if (text.IndexOf("EntitasGeneration", StringComparison.Ordinal) >= 0)
                return true;

            if (!state.filesMode)
                return false;

            return known || Signals.Any(signal => text.IndexOf(signal, StringComparison.Ordinal) >= 0);
        }

        // -- request / response -------------------------------------------------------------

        static GenerationRequest BuildRequest(bool check, IReadOnlyCollection<string>? extraSources)
        {
            var root = EntitasTools.ProjectRoot;
            var request = new GenerationRequest { projectRoot = root, check = check };

            var extraByAssembly = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (extraSources != null)
            {
                foreach (var path in extraSources.Where(p => File.Exists(Path.Combine(root, p))))
                {
                    var assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(path);
                    if (string.IsNullOrEmpty(assemblyName))
                        continue;

                    assemblyName = Path.GetFileNameWithoutExtension(assemblyName);
                    if (!extraByAssembly.TryGetValue(assemblyName, out var list))
                        extraByAssembly[assemblyName] = list = new List<string>();
                    list.Add(path);
                }
            }

            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                // Only assemblies that can declare components; the rest are not worth a byte of JSON.
                if (!assembly.allReferences.Any(r => string.Equals(Path.GetFileName(r), "Entitas.dll", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var sources = assembly.sourceFiles.ToList();
                if (extraByAssembly.TryGetValue(assembly.name, out var extra))
                    sources.AddRange(extra.Where(path => !sources.Contains(path)));

                request.assemblies.Add(new RequestedAssembly
                {
                    name = assembly.name,
                    sourceFiles = sources,
                    references = assembly.allReferences.Select(r => Path.GetFullPath(Path.Combine(root, r))).ToList(),
                    defines = assembly.defines.ToList(),
                    languageVersion = assembly.compilerOptions.LanguageVersion ?? string.Empty
                });
            }

            return request;
        }

        static int _requestCounter;

        static (string Request, string Response) WriteRequest(GenerationRequest request)
        {
            var id = ++_requestCounter;
            var requestPath = Path.Combine(EntitasTools.ScratchDirectory, $"request-{id}.json");
            var responsePath = Path.Combine(EntitasTools.ScratchDirectory, $"response-{id}.json");
            File.WriteAllText(requestPath, JsonUtility.ToJson(request));
            if (File.Exists(responsePath))
                File.Delete(responsePath);
            return (requestPath, responsePath);
        }

        static GenerationResponse? ReadResponse(EntitasTools.Result result, string responsePath)
        {
            if (!File.Exists(responsePath))
            {
                Debug.LogError(
                    $"Entitas: {EntitasTools.GeneratorName} failed (exit {result.ExitCode}) using '{EntitasTools.Dotnet()}'.\n{result.Error}{result.Output}");
                return null;
            }

            try
            {
                return JsonUtility.FromJson<GenerationResponse>(File.ReadAllText(responsePath));
            }
            finally
            {
                TryDelete(responsePath);
                TryDelete(responsePath.Replace("response-", "request-"));
            }
        }

        /// <summary>Logs the outcome, remembers what it learned and refreshes when files changed. False on errors.</summary>
        static bool Apply(GenerationResponse response, bool refresh, bool logUnchanged)
        {
            if (response.error.Length > 0)
            {
                Debug.LogError($"Entitas: {response.error}");
                return false;
            }

            var ok = true;
            var changed = false;
            var state = new GenerationState { known = true };

            foreach (var assembly in response.assemblies)
            {
                foreach (var warning in assembly.warnings)
                    Debug.LogWarning($"Entitas ({assembly.name}): {warning}");
                foreach (var error in assembly.errors)
                    Debug.LogError($"Entitas ({assembly.name}): {error}");
                ok &= assembly.errors.Count == 0;

                state.generatingAssemblies.Add(assembly.name);
                state.componentFiles.AddRange(assembly.componentFiles);
                if (assembly.mode == "Files")
                {
                    state.filesMode = true;
                    state.outputDirectories.Add(assembly.outputDirectory);
                }

                var assemblyChanged = assembly.written.Count > 0 || assembly.deleted.Count > 0;
                changed |= assemblyChanged;

                if (assembly.stale.Length > 0)
                    Debug.LogWarning($"Entitas ({assembly.name}): generated files are out of date — {assembly.stale}. Run {MenuGenerate}.");
                else if (assemblyChanged)
                    Debug.Log($"Entitas ({assembly.name}): {Describe(assembly)} in {response.milliseconds} ms.\n" +
                              string.Join("\n", assembly.written.Select(p => "+ " + p).Concat(assembly.deleted.Select(p => "- " + p))));
                else if (logUnchanged)
                    Debug.Log(assembly.mode == "Files"
                        ? $"Entitas ({assembly.name}): {assembly.outputDirectory} is up to date ({response.milliseconds} ms)."
                        : $"Entitas ({assembly.name}): generates with the source generator — nothing to write.");
            }

            if (response.assemblies.Count == 0 && logUnchanged)
                Debug.LogWarning("Entitas: no assembly declares [assembly: ContextDefinition(\"…\")] — nothing to generate.");

            SaveState(state);

            if (changed && refresh)
                AssetDatabase.Refresh();

            return ok;
        }

        static string Describe(AssemblyReport assembly)
        {
            var parts = new List<string>();
            if (assembly.written.Count > 0)
                parts.Add($"{assembly.written.Count} written");
            if (assembly.deleted.Count > 0)
                parts.Add($"{assembly.deleted.Count} deleted");
            parts.Add($"{assembly.unchanged} unchanged");
            return string.Join(", ", parts);
        }

        static void SaveState(GenerationState state)
        {
            _state = state;
            try
            {
                File.WriteAllText(StatePath, JsonUtility.ToJson(state));
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        /// First run in this Library: read the mode from the assemblies the editor has loaded
        /// (the last successful compile). Good enough to decide whether to watch at all;
        /// the first real run replaces it.
        /// </summary>
        static GenerationState GuessStateFromCompiledAssemblies()
        {
            var state = new GenerationState();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                IList<System.Reflection.CustomAttributeData> attributes;
                try
                {
                    attributes = assembly.GetCustomAttributesData();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var attribute in attributes)
                {
                    if (attribute.AttributeType.FullName != "Entitas.CodeGeneration.Attributes.EntitasGenerationAttribute")
                        continue;

                    if (attribute.NamedArguments.Any(a => a.MemberName == "Output" && Convert.ToInt32(a.TypedValue.Value) == 1))
                        state.filesMode = true;
                }
            }

            return state;
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
