using System;
using System.Collections.Generic;
using System.Linq;
using Entitas.Unity.Editor.Generation;
using FluentAssertions;
using Xunit;
using static Entitas.Unity.Editor.Generation.GenerationCommand;

namespace Entitas.Unity.Editor.Tests
{
    /// <summary>
    /// The order automatic generation keeps with Unity's refresh and compile. What must hold:
    /// the asset database is locked the moment a component is saved (so the refresh the IDE
    /// asks for waits), nothing runs while Unity compiles (deleting under a compile fails it),
    /// and a refresh is added only when a compilation already went ahead without the new files.
    /// </summary>
    public class GenerationSchedulerTests
    {
        static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);
        static readonly DateTime T0 = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        static DateTime At(int milliseconds) => T0.AddMilliseconds(milliseconds);

        readonly GenerationScheduler _scheduler = new GenerationScheduler(Quiet);

        [Fact]
        public void A_save_locks_at_once_runs_once_settled_and_unlocks_without_a_refresh_of_its_own()
        {
            _scheduler.SourcesChanged(new[] { "Assets/Components.cs" }, At(0), alreadyCompiled: false)
                .Should().Equal(LockAssetDatabase);

            _scheduler.Tick(At(100), isCompiling: false).Should().BeEmpty("the save has not settled yet");
            _scheduler.Tick(At(160), isCompiling: false).Should().Equal(StartRun);
            _scheduler.RunPaths.Should().Equal("Assets/Components.cs");

            // The refresh the IDE requested meanwhile waited on the lock; unlocking runs it,
            // with the generated files already on disk — one compilation.
            _scheduler.RunFinished(changedFiles: true).Should().Equal(UnlockAssetDatabase);
            _scheduler.IsIdle.Should().BeTrue();
        }

        [Fact]
        public void A_burst_of_saves_becomes_one_run()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), false);
            _scheduler.SourcesChanged(new[] { "B.cs" }, At(100), false).Should().BeEmpty("already locked");
            _scheduler.Tick(At(200), false).Should().BeEmpty("the burst is still going");
            _scheduler.Tick(At(260), false).Should().Equal(StartRun);
            _scheduler.RunPaths.Should().BeEquivalentTo("A.cs", "B.cs");
        }

        [Fact]
        public void Saves_during_a_run_keep_the_lock_for_a_second_run()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), false);
            _scheduler.Tick(At(200), false).Should().Equal(StartRun);

            _scheduler.SourcesChanged(new[] { "B.cs" }, At(300), false).Should().BeEmpty();
            _scheduler.Tick(At(500), false).Should().BeEmpty("one run at a time");
            _scheduler.RunFinished(changedFiles: true).Should().BeEmpty("B.cs still needs generating");
            _scheduler.IsLocked.Should().BeTrue();

            _scheduler.Tick(At(600), false).Should().Equal(StartRun);
            _scheduler.RunPaths.Should().Equal("B.cs");
            _scheduler.RunFinished(changedFiles: false).Should().Equal(UnlockAssetDatabase);
        }

        [Fact]
        public void Never_runs_while_unity_compiles_and_refreshes_after_a_compile_that_beat_it()
        {
            // The refresh got in before the lock: Unity is compiling the stale files.
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), false);

            _scheduler.Tick(At(200), isCompiling: true).Should().BeEmpty("files must not change under a running compile");
            _scheduler.Tick(At(900), isCompiling: true).Should().BeEmpty();
            _scheduler.Tick(At(1000), isCompiling: false).Should().Equal(StartRun);

            _scheduler.RunFinished(changedFiles: true).Should().Equal(UnlockAssetDatabase, Refresh);
        }

        [Fact]
        public void A_compilation_that_starts_while_changes_are_held_back_gets_a_refresh_after_it()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), false);
            _scheduler.CompilationStarted();
            _scheduler.Tick(At(200), false).Should().Equal(StartRun);
            _scheduler.RunFinished(changedFiles: true).Should().Equal(UnlockAssetDatabase, Refresh);
        }

        [Fact]
        public void Changes_unity_already_compiled_are_refreshed_after_generation()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), alreadyCompiled: true).Should().Equal(LockAssetDatabase);
            _scheduler.Tick(At(200), false).Should().Equal(StartRun);
            _scheduler.RunFinished(changedFiles: true).Should().Equal(UnlockAssetDatabase, Refresh);
        }

        [Fact]
        public void Nothing_written_means_no_refresh_and_no_compilation()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), alreadyCompiled: true);
            _scheduler.Tick(At(200), false);
            _scheduler.RunFinished(changedFiles: false).Should().Equal(UnlockAssetDatabase);
        }

        [Fact]
        public void A_refresh_need_does_not_leak_into_the_next_lock()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), alreadyCompiled: true);
            _scheduler.Tick(At(200), false);
            _scheduler.RunFinished(changedFiles: false);

            _scheduler.SourcesChanged(new[] { "A.cs" }, At(1000), alreadyCompiled: false);
            _scheduler.Tick(At(1200), false);
            _scheduler.RunFinished(changedFiles: true).Should().Equal(UnlockAssetDatabase);
        }

        [Fact]
        public void An_explicit_run_while_busy_runs_after_and_imports_what_it_changed()
        {
            _scheduler.RunRequested(At(0)).Should().Equal(LockAssetDatabase);
            _scheduler.Tick(At(0), isCompiling: true).Should().BeEmpty();
            _scheduler.Tick(At(50), isCompiling: false).Should().Equal(StartRun);
            _scheduler.RunFinished(changedFiles: true).Should().Equal(UnlockAssetDatabase, Refresh);
        }

        [Fact]
        public void Resetting_unlocks_once_and_a_late_run_result_does_not_unlock_again()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), false);
            _scheduler.Tick(At(200), false).Should().Equal(StartRun);

            _scheduler.Reset().Should().Equal(UnlockAssetDatabase);
            _scheduler.RunFinished(changedFiles: true).Should().BeEmpty();
            _scheduler.IsIdle.Should().BeTrue();
        }

        [Fact]
        public void Re_reporting_known_paths_changes_nothing()
        {
            _scheduler.SourcesChanged(new[] { "A.cs" }, At(0), false);
            _scheduler.SourcesChanged(new[] { "a.cs" }, At(100), false).Should().BeEmpty();
            _scheduler.Tick(At(160), false).Should().Equal(new[] { StartRun }, "the duplicate did not restart the quiet period");
        }

        [Fact]
        public void Locks_and_unlocks_always_pair_up_whatever_happens()
        {
            var random = new Random(1234);
            for (var round = 0; round < 200; round++)
            {
                var scheduler = new GenerationScheduler(Quiet);
                var depth = 0;
                var now = 0;

                void Apply(IEnumerable<GenerationCommand> commands)
                {
                    foreach (var command in commands)
                    {
                        if (command == LockAssetDatabase) depth++;
                        if (command == UnlockAssetDatabase) depth--;
                        depth.Should().BeInRange(0, 1, "StartAssetEditing/StopAssetEditing must never nest or go negative");
                        if (command == StartRun || command == Refresh)
                            depth.Should().Be(command == StartRun ? 1 : 0, "runs happen under the lock, refreshes after it");
                    }
                }

                var running = false;
                for (var step = 0; step < 60; step++)
                {
                    now += random.Next(0, 300);
                    switch (random.Next(6))
                    {
                        case 0: Apply(scheduler.SourcesChanged(new[] { $"F{random.Next(4)}.cs" }, At(now), random.Next(2) == 0)); break;
                        case 1: Apply(scheduler.RunRequested(At(now))); break;
                        case 2: scheduler.CompilationStarted(); break;
                        case 3:
                            var commands = scheduler.Tick(At(now), random.Next(3) == 0);
                            running |= commands.Contains(StartRun);
                            Apply(commands);
                            break;
                        case 4:
                            if (running) { running = false; Apply(scheduler.RunFinished(random.Next(2) == 0)); }
                            break;
                        case 5:
                            if (random.Next(10) == 0) { running = false; Apply(scheduler.Reset()); }
                            break;
                    }
                }

                Apply(scheduler.Reset());
                depth.Should().Be(0);
            }
        }
    }
}
