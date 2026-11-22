using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Entitas.SourceGenerator.Cli.Tests
{
    /// <summary>
    /// A throwaway Unity-shaped project on disk (ProjectSettings/, Assets/…) whose
    /// assembly is compiled the way the Unity editor describes it to entitas-gen: every .cs
    /// under Assets/, generated files included.
    /// </summary>
    public sealed class UnityProjectFixture : IDisposable
    {
        public string Root { get; }

        public UnityProjectFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "entitas-gen-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(Root, "Assets"));
        }

        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Delete(string relative) => File.Delete(PathOf(relative));

        public bool Exists(string relative) => File.Exists(PathOf(relative)) || Directory.Exists(PathOf(relative));

        public string Read(string relative) => File.ReadAllText(PathOf(relative));

        public IReadOnlyList<string> SourceFiles() =>
            Directory.EnumerateFiles(PathOf("Assets"), "*.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        /// <summary>Generated files currently on disk, project-relative.</summary>
        public IReadOnlyList<string> GeneratedFiles() =>
            SourceFiles()
                .Where(path => SourceGenerator.GeneratedFiles.IsGenerated(File.ReadAllText(path)))
                .Select(path => Relative(path))
                .ToList();

        public string Relative(string path) => GeneratedFileWriter.Relative(path, Root);

        public static IReadOnlyList<string> References()
        {
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            return tpa.Split(Path.PathSeparator)
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Append(typeof(Entitas.IComponent).Assembly.Location)
                .Append(typeof(Entitas.CodeGeneration.Attributes.ContextAttribute).Assembly.Location)
                .ToList();
        }

        public Compilation Compile() =>
            ProjectLoader.Compile("Assembly-CSharp", new ProjectLoader.ProjectInfo(
                SourceFiles(), new string[0], References(), new string[0]));

        public AssemblyResult Generate(bool check = false) =>
            GeneratedFileWriter.Run(Compile(), Root, new WriterOptions { Check = check });

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
