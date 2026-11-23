using System.Collections.Generic;
using Entitas.Unity.Editor.Generation;
using FluentAssertions;
using Xunit;

namespace Entitas.Unity.Editor.Tests
{
    /// <summary>
    /// "Generate Automatically" belongs to the project it was set in. In 0.5.0 it lived in
    /// EditorPrefs, shared by every project on the machine, and came back off in a project
    /// nobody had turned it off in — after which compiles used stale generated files.
    /// </summary>
    public class GenerationSettingsTests
    {
        /// <summary>One project's UserSettings.</summary>
        sealed class ProjectStore : IProjectSettingsStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
            public void Set(string key, string value) => Values[key] = value;
        }

        readonly ProjectStore _project = new ProjectStore();

        [Fact]
        public void Is_on_in_a_project_that_never_set_it()
        {
            new GenerationSettings(_project).Automatic.Should().BeTrue();
        }

        [Fact]
        public void Turned_off_it_stays_off_across_a_domain_reload()
        {
            new GenerationSettings(_project).Automatic = false;

            // A domain reload builds the settings anew; the value comes from the store.
            new GenerationSettings(_project).Automatic.Should().BeFalse();
        }

        [Fact]
        public void Turned_back_on_it_reads_on()
        {
            var settings = new GenerationSettings(_project) { Automatic = false };
            settings.Automatic = true;

            settings.Automatic.Should().BeTrue();
            new GenerationSettings(_project).Automatic.Should().BeTrue();
        }

        [Fact]
        public void Another_project_turning_it_off_leaves_this_one_on()
        {
            var other = new ProjectStore();
            new GenerationSettings(other).Automatic = false;

            new GenerationSettings(_project).Automatic.Should().BeTrue();
            new GenerationSettings(other).Automatic.Should().BeFalse();
        }

        [Fact]
        public void Every_read_goes_to_the_store_so_nothing_stale_is_kept()
        {
            var settings = new GenerationSettings(_project);
            settings.Automatic.Should().BeTrue();

            _project.Values[GenerationSettings.AutomaticKey] = "false";

            settings.Automatic.Should().BeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("0")]
        [InlineData("True")]
        public void Only_an_explicit_off_turns_it_off(string stored)
        {
            _project.Values[GenerationSettings.AutomaticKey] = stored;

            new GenerationSettings(_project).Automatic.Should().BeTrue();
        }

        [Theory]
        [InlineData("false")]
        [InlineData("False")]
        [InlineData(" false ")]
        public void Off_is_read_whatever_its_case(string stored)
        {
            _project.Values[GenerationSettings.AutomaticKey] = stored;

            new GenerationSettings(_project).Automatic.Should().BeFalse();
        }
    }
}
