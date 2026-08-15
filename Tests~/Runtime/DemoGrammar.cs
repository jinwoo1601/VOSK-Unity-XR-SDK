// ============================================================================
// Purpose:  Test copy of the demo command grammar, for acoustic replay and grammar analysis
// Layer:    Tests.Runtime
// Owns:     DemoGrammar (internal static class)
// Depends:  VoxrSlotDefinition, VoxrCommandDefinition, VoxrCommandSet, VoxrCommandRecogniser
// ============================================================================
// Hand-synced copy of Samples~/CommandRecognition/CommandDemo.cs:33-158: the
// fixture corpus (Tests~/Fixtures/audio/manifest.json) targets the shipped demo
// patterns, but Samples~ is not compiled into any test-referencable assembly
// (architecture.md D-8). If the demo grammar changes, update this class AND the
// fixture manifest together.
using System.Collections.Generic;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    internal static class DemoGrammar
    {
        static void Build(out VoxrSlotDefinition[] slots, out VoxrCommandSet[] sets)
        {
            var targets = new VoxrSlotDefinition(
                "target",
                new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" }
            );

            var weapons = new VoxrSlotDefinition(
                "weapon",
                new[] { "missiles", "torpedoes", "jackal" },
                aliases: new Dictionary<string, string> { { "jackals", "jackal" } }
            );

            var quantity = new VoxrSlotDefinition(
                "quantity",
                new[] { "all", "one", "two", "three" },
                aliases: new Dictionary<string, string> { { "a", "one" } }
            );

            var namedRange = new VoxrSlotDefinition(
                "range",
                new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" }
            );

            var heading = VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
            var elevation = VoxrSlotDefinition.NumberSequence(
                "elevation",
                minWords: 1,
                maxWords: 2
            );

            var weaponsSet = new VoxrCommandSet(
                "weapons",
                new[]
                {
                    new VoxrCommandDefinition(
                        "launch_weapon",
                        new[]
                        {
                            new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                            new[] { "fire", "{?quantity}", "{weapon}", "at", "{target}" },
                            new[] { "shoot", "{weapon}" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "cease_fire",
                        new[]
                        {
                            new[] { "cease", "fire" },
                            new[] { "stop", "firing" },
                            new[] { "disengage" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "resume_fire",
                        new[]
                        {
                            new[] { "resume", "fire" },
                            new[] { "resume", "firing" },
                            new[] { "reengage" },
                        }
                    ),
                }
            );

            var navigationSet = new VoxrCommandSet(
                "navigation",
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_distance_named",
                        new[]
                        {
                            new[] { "close", "distance", "{range}", "target", "{target}" },
                            new[] { "set", "distance", "{range}", "target", "{target}" },
                            new[] { "make", "distance", "{range}", "target", "{target}" },
                            new[] { "open", "distance", "{range}", "target", "{target}" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "approach_target",
                        new[]
                        {
                            new[] { "close", "on", "target", "{target}" },
                            new[] { "close", "in", "on", "target", "{target}" },
                            new[] { "approach", "target", "{target}" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "retreat_from_target",
                        new[]
                        {
                            new[] { "fall", "back", "from", "target", "{target}" },
                            new[] { "pull", "back", "from", "target", "{target}" },
                            new[] { "get", "away", "from", "target", "{target}" },
                            new[] { "move", "away", "from", "target", "{target}" },
                            new[] { "open", "distance", "from", "target", "{target}" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "set_heading",
                        new[]
                        {
                            new[] { "orient", "heading", "{heading}" },
                            new[] { "orient", "heading", "{heading}", "?mark", "{?elevation}" },
                            new[] { "set", "heading", "{heading}" },
                        }
                    ),
                }
            );

            var commonSet = new VoxrCommandSet(
                "common",
                new[]
                {
                    new VoxrCommandDefinition(
                        "mode_weapons",
                        new[] { new[] { "weapons", "mode" }, new[] { "switch", "to", "weapons" } }
                    ),
                    new VoxrCommandDefinition(
                        "mode_navigation",
                        new[]
                        {
                            new[] { "navigation", "mode" },
                            new[] { "switch", "to", "navigation" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "mode_all",
                        new[] { new[] { "all", "modes" }, new[] { "enable", "all" } }
                    ),
                    new VoxrCommandDefinition(
                        "mode_disable",
                        new[] { new[] { "disable", "all" }, new[] { "disable", "commands" } }
                    ),
                }
            );

            slots = new[] { targets, weapons, quantity, namedRange, heading, elevation };
            sets = new[] { weaponsSet, navigationSet, commonSet };
        }

        // The sets this grammar activates, in the order it activates them. Both Configure and
        // AllCommands read it, so the flatten below cannot drift from what the parser builds.
        static readonly string[] ActiveSetNames = { "weapons", "navigation", "common" };

        internal static void Configure(VoxrCommandRecogniser commandRecogniser)
        {
            Build(out var slots, out var sets);
            commandRecogniser.Configure(slots: slots, sets: sets);
            commandRecogniser.SetActiveSets(ActiveSetNames);
        }

        // Every command the demo grammar registers, flattened the way the parser sees them.
        // Exists so a test can measure the shipped grammar directly rather than re-transcribing
        // it — a hand transcription of this grammar is how the issue #74 warning-volume
        // measurement first got its numbers wrong.
        //
        // Flattened by ACTIVATION order, not by the order Build happens to declare the sets in:
        // CommandSetManager.Activate concatenates in the order the names are passed, so walking
        // the declaration array would only coincidentally agree, and reordering the activation
        // list would leave a measurement pinned against a grammar no parser ever builds.
        // The slots the demo grammar registers, for a test that needs to build a parser over
        // this grammar rather than a recogniser.
        internal static VoxrSlotDefinition[] AllSlots()
        {
            Build(out var slots, out _);
            return slots;
        }

        internal static VoxrCommandDefinition[] AllCommands()
        {
            Build(out _, out var sets);

            var byName = new Dictionary<string, VoxrCommandSet>();
            foreach (var set in sets)
                byName[set.Name] = set;

            var all = new List<VoxrCommandDefinition>();
            foreach (var name in ActiveSetNames)
                all.AddRange(byName[name].Commands);
            return all.ToArray();
        }
    }
}
