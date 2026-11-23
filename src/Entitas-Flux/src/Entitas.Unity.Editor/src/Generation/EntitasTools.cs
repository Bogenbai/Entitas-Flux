#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Entitas.Unity.Editor.Generation
{
    /// <summary>
    /// Finds and runs the command-line tools that ship inside the package (Tools~/:
    /// entitas-gen and entitas-rename). They run out of process on the .NET runtime the
    /// Unity editor itself ships with, so nothing has to be installed, and Roslyn never has
    /// to be loaded into the editor's own domain (where another package's copy would clash).
    /// </summary>
    static class EntitasTools
    {
        public const string GeneratorName = "entitas-gen";
        public const string RenameName = "entitas-rename";

        // These two stay in EditorPrefs, unlike "Generate Automatically": they are paths on this
        // machine, meaningless on another, set by hand and never written by the package. Mind
        // that a ToolsPath then serves every project on the machine, whatever its package version.

        /// <summary>EditorPrefs key: a folder holding entitas-gen.dll, overriding the bundled copy.</summary>
        public const string ToolsPathPreference = "Entitas.Generation.ToolsPath";

        /// <summary>EditorPrefs key: a dotnet executable, overriding the one Unity ships.</summary>
        public const string DotnetPathPreference = "Entitas.Generation.DotnetPath";

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath)!;

        public static string ScratchDirectory
        {
            get
            {
                var directory = Path.Combine(ProjectRoot, "Library", "EntitasFlux");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        public sealed class Result
        {
            public int ExitCode;
            public string Output = string.Empty;
            public string Error = string.Empty;
            public long Milliseconds;
        }

        public static string? FindTool(string name)
        {
            var file = name + ".dll";

            var overridden = EditorPrefs.GetString(ToolsPathPreference, string.Empty);
            if (overridden.Length > 0 && File.Exists(Path.Combine(overridden, file)))
                return Path.Combine(overridden, file);

            // Package layout: Editor/Entitas.Unity.Editor.dll next to Tools~/. A manual install
            // keeps the same relative layout (Assets/Entitas/Entitas/{Editor,Tools~}).
            var candidates = new List<string>();
            var location = typeof(EntitasTools).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var editorFolder = Path.GetDirectoryName(location)!;
                candidates.Add(Path.Combine(editorFolder, "..", "Tools~", file));
                candidates.Add(Path.Combine(editorFolder, "Tools~", file));
            }

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(EntitasTools).Assembly);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                candidates.Add(Path.Combine(package.resolvedPath, "Tools~", file));

            return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        static string? _dotnet;

        /// <summary>
        /// The dotnet host Unity ships for its own build pipeline (Unity 2022.3 and 6 both
        /// carry a .NET 6 runtime), found once per domain; a dotnet on PATH otherwise.
        /// </summary>
        public static string Dotnet()
        {
            var overridden = EditorPrefs.GetString(DotnetPathPreference, string.Empty);
            if (overridden.Length > 0 && File.Exists(overridden))
                return overridden;

            if (_dotnet != null)
                return _dotnet;

            var exe = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            var contents = EditorApplication.applicationContentsPath;
            var known = new[]
            {
                Path.Combine(contents, "NetCoreRuntime", exe),
                Path.Combine(contents, "Resources", "Scripting", "NetCoreRuntime", exe),
                Path.Combine(contents, "Scripting", "NetCoreRuntime", exe),
                Path.Combine(contents, "Data", "NetCoreRuntime", exe)
            };

            _dotnet = known.FirstOrDefault(File.Exists);
            if (_dotnet == null)
            {
                try
                {
                    _dotnet = Directory
                        .EnumerateDirectories(contents, "NetCoreRuntime", SearchOption.AllDirectories)
                        .Select(directory => Path.Combine(directory, exe))
                        .FirstOrDefault(File.Exists);
                }
                catch (Exception)
                {
                    // An unreadable folder in the editor install; fall back to PATH.
                }
            }

            return _dotnet ??= exe;
        }

        public static ProcessStartInfo StartInfo(string tool, IEnumerable<string> arguments) => new ProcessStartInfo
        {
            FileName = Dotnet(),
            Arguments = string.Join(" ", new[] { tool }.Concat(arguments).Select(Quote)),
            WorkingDirectory = ProjectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        /// <summary>Runs a tool to completion on the calling thread.</summary>
        public static Result Run(string tool, IEnumerable<string> arguments, int timeoutMilliseconds = 300_000)
        {
            var stopwatch = Stopwatch.StartNew();
            using var process = new Process { StartInfo = StartInfo(tool, arguments) };
            var output = new StringBuilder();
            var error = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(); } catch (Exception) { }
                return new Result { ExitCode = -1, Error = $"{Path.GetFileName(tool)} did not finish in {timeoutMilliseconds / 1000} s" };
            }

            process.WaitForExit(); // flush the async readers
            return new Result
            {
                ExitCode = process.ExitCode,
                Output = output.ToString(),
                Error = error.ToString(),
                Milliseconds = stopwatch.ElapsedMilliseconds
            };
        }

        static string Quote(string argument) =>
            argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
                ? argument
                : "\"" + argument.Replace("\"", "\\\"") + "\"";
    }
}
