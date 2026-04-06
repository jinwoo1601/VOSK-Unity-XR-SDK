using System.Collections.Generic;
using UnityEngine;
using VoskXR;
using VoskXR.Commands;

public class CommandDemo : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskCommandRecogniser commandRecogniser;

    // Define intent names as constants to avoid typos.
    static class Intents
    {
        public const string LaunchWeapon = "launch_weapon";
        public const string CeaseFire = "cease_fire";
        public const string ResumeFire = "resume_fire";
        public const string SetDistanceNamed = "set_distance_named";
        public const string ApproachTarget = "approach_target";
        public const string RetreatFromTarget = "retreat_from_target";
        public const string SetHeading = "set_heading";
    }

    void Start()
    {
        var targets = new VoskSlotDefinition("target",
            new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" });

        var weapons = new VoskSlotDefinition("weapon",
            new[] { "missiles", "torpedoes", "jackal" },
            aliases: new Dictionary<string, string>
            {
                { "jackals", "jackal" },
            });

        var quantity = new VoskSlotDefinition("quantity",
            new[] { "all", "one", "two", "three" },
            aliases: new Dictionary<string, string>
            {
                { "a", "one" },
            });

        var namedRange = new VoskSlotDefinition("range",
            new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" });

        var heading = VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
        var elevation = VoskSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2);

        var weaponsSet = new VoskCommandSet("weapons", new[]
        {
            new VoskCommandDefinition(Intents.LaunchWeapon, new[]
            {
                new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                new[] { "fire", "{?quantity}", "{weapon}", "at", "{target}" },
                new[] { "shoot", "{weapon}" },
            }),
            new VoskCommandDefinition(Intents.CeaseFire, new[]
            {
                new[] { "cease", "fire" },
                new[] { "stop", "firing" },
                new[] { "disengage" },
            }),
            new VoskCommandDefinition(Intents.ResumeFire, new[]
            {
                new[] { "resume", "fire" },
                new[] { "resume", "firing" },
                new[] { "reengage" },
            }),
        });

        var navigationSet = new VoskCommandSet("navigation", new[]
        {
            new VoskCommandDefinition(Intents.SetDistanceNamed, new[]
            {
                new[] { "close", "distance", "{range}", "target", "{target}" },
                new[] { "set", "distance", "{range}", "target", "{target}" },
                new[] { "make", "distance", "{range}", "target", "{target}" },
                new[] { "open", "distance", "{range}", "target", "{target}" },
            }),
            new VoskCommandDefinition(Intents.ApproachTarget, new[]
            {
                new[] { "close", "on", "target", "{target}" },
                new[] { "close", "in", "on", "target", "{target}" },
                new[] { "approach", "target", "{target}" },
            }),
            new VoskCommandDefinition(Intents.RetreatFromTarget, new[]
            {
                new[] { "fall", "back", "from", "target", "{target}" },
                new[] { "pull", "back", "from", "target", "{target}" },
                new[] { "get", "away", "from", "target", "{target}" },
                new[] { "move", "away", "from", "target", "{target}" },
                new[] { "open", "distance", "from", "target", "{target}" },
            }),
            new VoskCommandDefinition(Intents.SetHeading, new[]
            {
                new[] { "orient", "heading", "{heading}" },
                new[] { "orient", "heading", "{heading}", "mark", "{?elevation}" },
                new[] { "set", "heading", "{heading}" },
            }),
        });

        var commonSet = new VoskCommandSet("common", Array.Empty<VoskCommandDefinition>());

        commandRecogniser.Configure(
            slots: new[] { targets, weapons, quantity, namedRange, heading, elevation },
            sets: new[] { weaponsSet, navigationSet, commonSet });

        // Activate all sets for demo — in a real game you'd activate per game state
        commandRecogniser.SetActiveSets("weapons", "navigation", "common");

        commandRecogniser.OnCommandRecognised += OnCommand;
        commandRecogniser.OnCommandsRecognised += OnCommandBatch;
        commandRecogniser.OnUnrecognisedSpeech += OnUnrecognised;

        recogniser.StartRecognition();
    }

    void OnDestroy()
    {
        if (commandRecogniser != null)
        {
            commandRecogniser.OnCommandRecognised -= OnCommand;
            commandRecogniser.OnCommandsRecognised -= OnCommandBatch;
            commandRecogniser.OnUnrecognisedSpeech -= OnUnrecognised;
        }
    }

    void OnCommand(VoskCommand cmd)
    {
        Debug.Log($"[CommandDemo] Command: {cmd.Intent} " +
            $"(confidence={cmd.Confidence:F2}, score={cmd.Score:F2})");

        switch (cmd.Intent)
        {
            case Intents.LaunchWeapon:
                string weapon = cmd.GetSlot("weapon");
                string qty = cmd.HasSlot("quantity") ? cmd.GetSlot("quantity") : "1";
                string target = cmd.GetSlot("target");
                Debug.Log($"[CommandDemo]   Launch {qty} {weapon} at {target}");
                break;

            case Intents.CeaseFire:
                Debug.Log("[CommandDemo]   Cease fire!");
                break;

            case Intents.ResumeFire:
                Debug.Log("[CommandDemo]   Resume fire!");
                break;

            case Intents.SetDistanceNamed:
                Debug.Log($"[CommandDemo]   Set distance {cmd.GetSlot("range")} " +
                    $"from {cmd.GetSlot("target")}");
                break;

            case Intents.ApproachTarget:
                Debug.Log($"[CommandDemo]   Approach {cmd.GetSlot("target")}");
                break;

            case Intents.RetreatFromTarget:
                Debug.Log($"[CommandDemo]   Retreat from {cmd.GetSlot("target")}");
                break;

            case Intents.SetHeading:
                string hdg = cmd.GetSlot("heading");
                int hdgVal = VoskNumberParser.ParseDigitSequence(hdg);
                string elevStr = cmd.HasSlot("elevation") ? cmd.GetSlot("elevation") : null;
                int elevVal = elevStr != null ? VoskNumberParser.ParseDigitSequence(elevStr) : -1;
                Debug.Log($"[CommandDemo]   Heading={hdgVal} (raw=\"{hdg}\"), Elevation={elevVal}");
                break;
        }
    }

    void OnCommandBatch(VoskCommand[] commands)
    {
        Debug.Log($"[CommandDemo] Batch: {commands.Length} command(s) from single utterance");
        for (int i = 0; i < commands.Length; i++)
            Debug.Log($"[CommandDemo]   [{i}] {commands[i].Intent} (score={commands[i].Score:F2})");
    }

    void OnUnrecognised(string text)
    {
        Debug.Log($"[CommandDemo] Unrecognised: \"{text}\"");
    }

    // --- Mode switching examples (call from UI or game state logic) ---

    public void SwitchToWeaponsMode()
    {
        commandRecogniser.SetActiveSets("weapons", "common");
        Debug.Log("[CommandDemo] Switched to WEAPONS mode");
    }

    public void SwitchToNavigationMode()
    {
        commandRecogniser.SetActiveSets("navigation", "common");
        Debug.Log("[CommandDemo] Switched to NAVIGATION mode");
    }
}
