using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VoskXR;
using VoskXR.Commands;

public class CommandDemo : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskCommandRecogniser commandRecogniser;

    [Tooltip("When enabled, skip the code-based Configure() call and rely on " +
             "ScriptableObject assets wired on VoskCommandRecogniser instead. " +
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

        var commonSet = new VoskCommandSet("common", new[]
        {
            new VoskCommandDefinition(Intents.ModeWeapons, new[]
            {
                new[] { "weapons", "mode" },
                new[] { "switch", "to", "weapons" },
            }),
            new VoskCommandDefinition(Intents.ModeNavigation, new[]
            {
                new[] { "navigation", "mode" },
                new[] { "switch", "to", "navigation" },
            }),
            new VoskCommandDefinition(Intents.ModeAll, new[]
            {
                new[] { "all", "modes" },
                new[] { "enable", "all" },
            }),
            new VoskCommandDefinition(Intents.ModeDisable, new[]
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

    void OnCommand(VoskCommand cmd)
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

    void OnCommandBatch(VoskCommand[] commands) { }

    void OnUnrecognised(string text) { }

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
