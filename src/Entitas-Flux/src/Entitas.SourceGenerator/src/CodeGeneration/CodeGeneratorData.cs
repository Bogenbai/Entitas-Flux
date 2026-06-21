using System.Collections.Generic;

namespace Entitas.SourceGenerator.CodeGeneration
{
    /// <summary>
    /// Local replacement for Jenny's <c>CodeGeneratorData</c> base class. It is a
    /// plain string-keyed property bag; all the typed accessors live in the
    /// extension classes (e.g. <c>ComponentDataExtensions</c>) and use the exact
    /// same dictionary key strings as the legacy Jenny plugins.
    /// </summary>
    public class CodeGeneratorData : Dictionary<string, object>
    {
        public CodeGeneratorData() { }

        public CodeGeneratorData(CodeGeneratorData other) : base(other) { }
    }
}
