using System;
using System.Collections.Generic;

// Fields are filled in by JsonUtility.
#pragma warning disable 0649
namespace Entitas.Unity.Editor.Generation
{
    // The JSON exchanged with entitas-gen (--request / --response). Mirrors
    // Entitas.SourceGenerator.Cli's UnityProtocol field for field; JsonUtility needs
    // [Serializable] classes with public fields.

    [Serializable]
    sealed class GenerationRequest
    {
        public string projectRoot = string.Empty;
        public bool check;
        public List<RequestedAssembly> assemblies = new List<RequestedAssembly>();
    }

    [Serializable]
    sealed class RequestedAssembly
    {
        public string name = string.Empty;
        public List<string> sourceFiles = new List<string>();
        public List<string> references = new List<string>();
        public List<string> defines = new List<string>();
        public string languageVersion = string.Empty;
    }

    [Serializable]
    sealed class GenerationResponse
    {
        public string error = string.Empty;
        public long milliseconds;
        public List<AssemblyReport> assemblies = new List<AssemblyReport>();
    }

    [Serializable]
    sealed class AssemblyReport
    {
        public string name = string.Empty;
        public string mode = string.Empty;
        public string outputDirectory = string.Empty;
        public List<string> written = new List<string>();
        public List<string> deleted = new List<string>();
        public int unchanged;
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
        public List<string> componentFiles = new List<string>();
        public string stale = string.Empty;
    }

    /// <summary>What the last run learned, kept in Library/ so automatic runs know when to fire.</summary>
    [Serializable]
    sealed class GenerationState
    {
        /// <summary>False until a run (or the compiled assemblies) said what the project uses.</summary>
        public bool known;

        /// <summary>Some assembly writes files (Output = GenerationOutput.Files).</summary>
        public bool filesMode;

        public List<string> generatingAssemblies = new List<string>();
        public List<string> outputDirectories = new List<string>();
        public List<string> componentFiles = new List<string>();
    }
}
