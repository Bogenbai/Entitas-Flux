#nullable enable
using System;

namespace Entitas.Unity.Editor.Generation
{
    /// <summary>A key/value store that belongs to one project. In the editor: <c>EditorUserSettings</c>.</summary>
    interface IProjectSettingsStore
    {
        /// <summary>The stored value, or null when the project never stored one.</summary>
        string? Get(string key);

        void Set(string key, string value);
    }

    /// <summary>
    /// The editor's generation settings, read from and written to the project's own store on
    /// every access. Nothing is cached here, so neither a domain reload (which clears statics)
    /// nor anything outside the project can leave a stale copy behind. Unity-free, so it is
    /// unit-tested; the editor hands it <c>EditorUserSettings</c>.
    ///
    /// Why per project: 0.5.0 kept "Generate Automatically" in EditorPrefs. On macOS that is
    /// one plist for the whole machine, and every running editor works from the copy it read
    /// at launch: it never sees what other editors write (not even after a domain reload), and
    /// its next write of any EditorPrefs key puts its whole copy back, reverting values other
    /// editors changed since and deleting keys they added. With two editors open the switch
    /// came back off in a project nobody turned it off in, and compiles used stale generated
    /// files. The old key is deliberately not read: its value may belong to any project on the
    /// machine, and wrongly off (stale files, broken compiles) is the costly mistake, while
    /// wrongly on costs a run that rewrites only what is out of date. A project starts with
    /// the default instead.
    ///
    /// Why EditorUserSettings and not a ProjectSettings asset: whether generation runs by
    /// itself is one developer's workflow choice, not the team's — the files are committed
    /// and up to date either way. UserSettings/ is per user and per project, and Unity's
    /// .gitignore keeps it out of version control, so toggling the menu neither changes a
    /// teammate's editor nor shows up as a diff.
    /// </summary>
    sealed class GenerationSettings
    {
        public const string AutomaticKey = "Entitas.Generation.Automatic";
        const string On = "true";
        const string Off = "false";

        readonly IProjectSettingsStore _store;

        public GenerationSettings(IProjectSettingsStore store) => _store = store;

        /// <summary>Whether a component change regenerates by itself. On unless this project turned it off.</summary>
        public bool Automatic
        {
            get => !string.Equals(_store.Get(AutomaticKey)?.Trim(), Off, StringComparison.OrdinalIgnoreCase);
            set => _store.Set(AutomaticKey, value ? On : Off);
        }
    }
}
