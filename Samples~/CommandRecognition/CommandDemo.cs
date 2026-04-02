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
    }

    void Start()
    {
        var targets = new VoskSlotDefinition("target",
            new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" });

        var weapons = new VoskSlotDefinition("weapon",
            new[] { "missiles", "torpedoes", "jackal", "jackals" });

        var quantity = new VoskSlotDefinition("quantity",
            new[] { "all", "one", "two", "three" });

        var namedRange = new VoskSlotDefinition("range",
            new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" });

        var commands = new[]
        {
            new VoskCommandDefinition(Intents.LaunchWeapon, new[]
            {
                new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                new[] { "launch", "a", "{weapon}", "target", "{target}" },
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
        };

        commandRecogniser.Configure(
            slots: new[] { targets, weapons, quantity, namedRange },
            commands: commands);

        commandRecogniser.OnCommandRecognised += OnCommand;
        commandRecogniser.OnUnrecognisedSpeech += OnUnrecognised;

        recogniser.StartRecognition();
    }

    void OnDestroy()
    {
        if (commandRecogniser != null)
        {
            commandRecogniser.OnCommandRecognised -= OnCommand;
            commandRecogniser.OnUnrecognisedSpeech -= OnUnrecognised;
        }
    }

    void OnCommand(VoskCommand cmd)
    {
        Debug.Log($"[CommandDemo] Command: {cmd.Intent} (confidence={cmd.Confidence:F2})");

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
        }
    }

    void OnUnrecognised(string text)
    {
        Debug.Log($"[CommandDemo] Unrecognised: \"{text}\"");
    }
}
