namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Local stand-in for Jenny's CodeGenFile so the ported generators stay almost
    /// verbatim. FileName is only used to derive a (globally unique) Roslyn hint
    /// name; legacy Jenny used it as a physical path and MERGED files sharing a
    /// name via MergeFilesPostProcessor. The source generator does NOT merge:
    /// every generated class is partial, so emitting each fragment as its own
    /// compilation unit produces the same end result.
    /// </summary>
    public sealed class CodeGenFile
    {
        public string FileName;
        public string FileContent;
        public string GeneratorName;

        public CodeGenFile(string fileName, string fileContent, string generatorName)
        {
            FileName = fileName;
            FileContent = fileContent;
            GeneratorName = generatorName;
        }
    }

    /// <summary>
    /// Local stand-in for Jenny's AbstractGenerator. The Jenny IConfigurable /
    /// IgnoreNamespacesConfig plumbing is dropped: ignoreNamespaces is handled
    /// centrally (set on CodeGeneratorExtensions before the generators run).
    /// </summary>
    public abstract class AbstractGenerator
    {
        public abstract string Name { get; }
        public abstract CodeGenFile[] Generate(CodeGeneratorData[] data);
    }
}
