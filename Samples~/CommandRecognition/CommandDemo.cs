using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VoXR;
using VoXR.Commands;

public class CommandDemo : MonoBehaviour
{
    [SerializeField] VoxrSpeechRecogniser recogniser;
    [SerializeField] VoxrCommandRecogniser commandRecogniser;

    [Tooltip("When enabled, skip the code-based Configure() call and rely on " +
             "ScriptableObject assets wired on VoxrCommandRecogniser instead. " +
             "Used for v2.5 Inspector authoring tests.")]
    [SerializeField] bool useInspectorAuthoring = false;

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
        public const string ModeWeapons = "mode_weapons";
        public const string ModeNavigation = "mode_navigation";
        public const string ModeAll = "mode_all";
        public const string ModeDisable = "mode_disable";
    }

    void Start()
    {
        if (useInspectorAuthoring)
        {
            // Slots/commands/sets came from ScriptableObject assets in Awake().
            // Just wire events and start recognition.
            commandRecogniser.OnCommandRecognised += OnCommand;
            commandRecogniser.OnCommandsRecognised += OnCommandBatch;
            commandRecogniser.OnUnrecognisedSpeech += OnUnrecognised;
            recogniser.StartRecognition();
            return;
        }

        var targets = new VoxrSlotDefinition("target",
            new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" });

        var weapons = new VoxrSlotDefinition("weapon",
            new[] { "missiles", "torpedoes", "jackal" },
            aliases: new Dictionary<string, string>
            {
                { "jackals", "jackal" },
            });

        var quantity = new VoxrSlotDefinition("quantity",
            new[] { "all", "one", "two", "three" },
            aliases: new Dictionary<string, string>
            {
                { "a", "one" },
            });

        var namedRange = new VoxrSlotDefinition("range",
            new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" });

        var heading = VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
        var elevation = VoxrSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2);

        var weaponsSet = new VoxrCommandSet("weapons", new[]
        {
            new VoxrCommandDefinition(Intents.LaunchWeapon, new[]
            {
                new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                new[] { "fire", "{?quantity}", "{weapon}", "at", "{target}" },
                new[] { "shoot", "{weapon}" },
            }),
            new VoxrCommandDefinition(Intents.CeaseFire, new[]
            {
                new[] { "cease", "fire" },
                new[] { "stop", "firing" },
                new[] { "disengage" },
            }),
            new VoxrCommandDefinition(Intents.ResumeFire, new[]
            {
                new[] { "resume", "fire" },
                new[] { "resume", "firing" },
                new[] { "reengage" },
            }),
        });

        var navigationSet = new VoxrCommandSet("navigation", new[]
        {
            new VoxrCommandDefinition(Intents.SetDistanceNamed, new[]
            {
                new[] { "close", "distance", "{range}", "target", "{target}" },
                new[] { "set", "distance", "{range}", "target", "{target}" },
                new[] { "make", "distance", "{range}", "target", "{target}" },
                new[] { "open", "distance", "{range}", "target", "{target}" },
            }),
            new VoxrCommandDefinition(Intents.ApproachTarget, new[]
            {
                new[] { "close", "on", "target", "{target}" },
                new[] { "close", "in", "on", "target", "{target}" },
                new[] { "approach", "target", "{target}" },
            }),
            new VoxrCommandDefinition(Intents.RetreatFromTarget, new[]
            {
                new[] { "fall", "back", "from", "target", "{target}" },
                new[] { "pull", "back", "from", "target", "{target}" },
                new[] { "get", "away", "from", "target", "{target}" },
                new[] { "move", "away", "from", "target", "{target}" },
                new[] { "open", "distance", "from", "target", "{target}" },
            }),
            new VoxrCommandDefinition(Intents.SetHeading, new[]
            {
                new[] { "orient", "heading", "{heading}" },
                        new[] { "orient", "heading", "{heading}", "?mark", "{?elevation}" },
                new[] { "set", "heading", "{heading}" },
            }),
        });

        var commonSet = new VoxrCommandSet("common", new[]
        {
            new VoxrCommandDefinition(Intents.ModeWeapons, new[]
            {
                new[] { "weapons", "mode" },
                new[] { "switch", "to", "weapons" },
            }),
            new VoxrCommandDefinition(Intents.ModeNavigation, new[]
            {
                new[] { "navigation", "mode" },
                new[] { "switch", "to", "navigation" },
            }),
            new VoxrCommandDefinition(Intents.ModeAll, new[]
            {
                new[] { "all", "modes" },
                new[] { "enable", "all" },
            }),
            new VoxrCommandDefinition(Intents.ModeDisable, new[]
            {
                new[] { "disable", "all" },
                new[] { "disable", "commands" },
            }),
        });

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

    void OnCommand(VoxrCommand cmd)
    {
        switch (cmd.Intent)
        {
            case Intents.ModeWeapons:
                SwitchToWeaponsMode();
                break;

            case Intents.ModeNavigation:
                SwitchToNavigationMode();
                break;

            case Intents.ModeAll:
                SwitchToAllModes();
                break;

            case Intents.ModeDisable:
                DisableAllModes();
                break;
        }
    }

    void OnCommandBatch(VoxrCommand[] commands) { }

    void OnUnrecognised(string text)
    {
        Debug.Log($"[CommandDemo] Unrecognised speech: \"{text}\"");
    }

    // --- Mode switching (voice-triggered or called from UI/game logic) ---

    public void SwitchToWeaponsMode()
    {
        commandRecogniser.SetActiveSets("weapons", "common");
    }

    public void SwitchToNavigationMode()
    {
        commandRecogniser.SetActiveSets("navigation", "common");
    }

    public void SwitchToAllModes()
    {
        commandRecogniser.SetActiveSets("weapons", "navigation", "common");
    }

    public void DisableAllModes()
    {
        commandRecogniser.SetActiveSets();
        StartCoroutine(RestoreAllModesAfterDelay(5f));
    }

    IEnumerator RestoreAllModesAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        commandRecogniser.SetActiveSets("weapons", "navigation", "common");
    }
}
