#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Entitas.Unity.Editor.Generation
{
    /// <summary>
    /// Tools > Entitas > Rename Component…: renames a component and every identifier the
    /// generator derives from its name (AddX, hasX, isX, GameMatcher.X, XChanged,
    /// IAnyXListener, …) across the project, through the entitas-rename that ships with the
    /// package — the same engine as the IDE's [RenameTo] quick fix. Preview first, then
    /// rename; with Output = GenerationOutput.Files the generated files are regenerated as
    /// part of it.
    /// </summary>
    sealed class RenameComponentWindow : EditorWindow
    {
        string _oldName = string.Empty;
        string _newName = string.Empty;
        bool _includeStrings;
        string _output = string.Empty;
        Vector2 _scroll;

        [MenuItem("Tools/Entitas/Rename Component...", false, EntitasMenuItemPriorities.rename_component)]
        static void Open()
        {
            var window = GetWindow<RenameComponentWindow>(true, "Rename Component");
            window.minSize = new Vector2(520, 360);
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Renames the component class, its file (with its .meta) and every generated member derived from its " +
                "name, in every assembly. Preview shows the full list first; nothing is written until Rename.",
                MessageType.None);

            _oldName = EditorGUILayout.TextField(new GUIContent("Component", "Health, HealthComponent, or My.Game.HealthComponent"), _oldName);
            _newName = EditorGUILayout.TextField(new GUIContent("New name"), _newName);
            _includeStrings = EditorGUILayout.Toggle(new GUIContent("Strings and comments", "Also rewrite the name inside string literals and comments."), _includeStrings);

            using (new EditorGUI.DisabledScope(_oldName.Trim().Length == 0 || _newName.Trim().Length == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Preview", GUILayout.Width(100)))
                    Run(apply: false);
                if (GUILayout.Button("Rename", GUILayout.Width(100)))
                    Run(apply: true);
            }

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.SelectableLabel(_output, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void Run(bool apply)
        {
            var tool = EntitasTools.FindTool(EntitasTools.RenameName);
            if (tool == null)
            {
                _output = $"{EntitasTools.RenameName}.dll was not found next to the package's editor assembly (Tools~/).";
                return;
            }

            // entitas-rename reads the project files the IDE package writes; bring them up to
            // date first, or a component added since the last sync would be invisible to it.
            try
            {
                global::Unity.CodeEditor.CodeEditor.CurrentEditor.SyncAll();
            }
            catch (Exception)
            {
                // No code editor configured; the csproj files may be missing or stale.
            }

            var arguments = new List<string> { _oldName.Trim(), _newName.Trim(), "-r", EntitasTools.ProjectRoot };
            var project = GeneratingProject();
            if (project != null)
                arguments.AddRange(new[] { "-p", project });
            if (_includeStrings)
                arguments.Add("-s");
            if (apply)
                arguments.Add("-a");

            EntitasTools.Result result;
            try
            {
                EditorUtility.DisplayProgressBar("Entitas", apply ? "Renaming component…" : "Planning rename…", 0.5f);
                result = EntitasTools.Run(tool, arguments);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            _output = (result.Output + result.Error).Trim();
            _scroll = Vector2.zero;

            if (apply && result.ExitCode == 0)
                AssetDatabase.Refresh();
        }

        /// <summary>The csproj of the assembly that declares the contexts, when it is not Assembly-CSharp.</summary>
        static string? GeneratingProject()
        {
            var statePath = Path.Combine(EntitasTools.ScratchDirectory, "state.json");
            if (!File.Exists(statePath))
                return null;

            var state = JsonUtility.FromJson<GenerationState>(File.ReadAllText(statePath));
            var assembly = state?.generatingAssemblies.FirstOrDefault();
            if (assembly == null)
                return null;

            var csproj = Path.Combine(EntitasTools.ProjectRoot, assembly + ".csproj");
            return File.Exists(csproj) ? csproj : null;
        }
    }
}
