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

        /// <summary>Renames (<see cref="RenameReport.key"/>) the user chose to update the usages of.</summary>
        public List<string> applyRenames = new List<string>();

        /// <summary>Renames the user chose to regenerate without updating.</summary>
        public List<string> ignoreRenames = new List<string>();

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
        public RenameReport rename = new RenameReport();
    }

    /// <summary>A component renamed in the IDE whose old generated API code still used; key is empty when none.</summary>
    [Serializable]
    sealed class RenameReport
    {
        public string key = string.Empty;
        public string oldName = string.Empty;
        public string newName = string.Empty;

        /// <summary>"pending" (nothing was written), "applied", "ignored" or "unused".</summary>
        public string state = string.Empty;

        public int usages;
        public List<string> files = new List<string>();
        public List<string> identifiers = new List<string>();
        public List<string> warnings = new List<string>();
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
