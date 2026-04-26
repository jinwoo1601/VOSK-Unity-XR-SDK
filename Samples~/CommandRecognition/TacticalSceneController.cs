using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VoXR.Commands;

// Drives the visual side of the CommandRecognition_Tactical scene:
//   - Maps the "target" slot value to a renderer and flashes it on relevant intents.
//   - Updates the mode chip from VoxrCommandRecogniser.ActiveSetNames.
//   - Maintains a rolling command log and a last-command inspector panel.
//
// Wire this component to the same GameObject as CommandDemo (or any GameObject
// that has a reference to the command recogniser).
public class TacticalSceneController : MonoBehaviour
{
    [Serializable]
    public struct TargetMapping
    {
        [Tooltip("Slot value that resolves to this target, e.g. \"hotel one\".")]
        public string slotValue;

        [Tooltip("Renderer whose material colour will flash on hit.")]
        public Renderer renderer;
    }

    [SerializeField] VoxrCommandRecogniser commandRecogniser;

    [Header("Targets")]
    [SerializeField] TargetMapping[] targets;

    [Header("UI")]
    [SerializeField] Text modeChipText;
    [SerializeField] Text commandLogText;
    [SerializeField] Text lastCommandText;

    [Header("Flash Colours")]
    [SerializeField] Color hitColor = new Color(0.9f, 0.2f, 0.2f, 1f);
    [SerializeField] Color approachColor = new Color(0.2f, 0.9f, 0.4f, 1f);
    [SerializeField] Color retreatColor = new Color(0.3f, 0.5f, 1f, 1f);
    [SerializeField] float flashSeconds = 1.2f;

    [Header("Log")]
    [SerializeField, Min(1)] int maxLogLines = 8;

    readonly Dictionary<string, Renderer> _byTarget = new Dictionary<string, Renderer>();
    readonly Dictionary<Renderer, Color> _baseColors = new Dictionary<Renderer, Color>();
    readonly Queue<string> _logLines = new Queue<string>();
    readonly StringBuilder _logBuilder = new StringBuilder(512);

    string _lastSetsSnapshot;

    void Awake()
    {
        if (targets == null) return;

        foreach (var mapping in targets)
        {
            if (mapping.renderer == null || string.IsNullOrEmpty(mapping.slotValue))
                continue;

            _byTarget[mapping.slotValue] = mapping.renderer;
            // Instance the material so colour changes don't leak into shared assets.
            _baseColors[mapping.renderer] = mapping.renderer.material.color;
        }
    }

    void OnEnable()
    {
        if (commandRecogniser == null) return;
        commandRecogniser.OnCommandRecognised += OnCommand;
        commandRecogniser.OnUnrecognisedSpeech += OnUnrecognised;
    }

    void OnDisable()
    {
        if (commandRecogniser == null) return;
        commandRecogniser.OnCommandRecognised -= OnCommand;
        commandRecogniser.OnUnrecognisedSpeech -= OnUnrecognised;
    }

    void Update()
    {
        if (commandRecogniser == null || modeChipText == null) return;

        string snapshot = ComposeSetsSnapshot(commandRecogniser.ActiveSetNames);
        if (snapshot == _lastSetsSnapshot) return;
        _lastSetsSnapshot = snapshot;
        modeChipText.text = $"MODE: {snapshot}";
    }

    static string ComposeSetsSnapshot(string[] sets)
    {
        if (sets == null || sets.Length == 0) return "(disabled)";
        return string.Join(" + ", sets);
    }

    void OnCommand(VoxrCommand cmd)
    {
        AppendLog($"{cmd.Intent} (score {cmd.Score:F2})");
        UpdateLastCommandPanel(cmd);

        switch (cmd.Intent)
        {
            case "launch_weapon":
                FlashTarget(cmd.GetSlot("target"), hitColor);
                break;

            case "approach_target":
            case "set_distance_named":
                FlashTarget(cmd.GetSlot("target"), approachColor);
                break;

            case "retreat_from_target":
                FlashTarget(cmd.GetSlot("target"), retreatColor);
                break;
        }
    }

    void OnUnrecognised(string text)
    {
        AppendLog($"<unrecognised> \"{text}\"");
    }

    void FlashTarget(string slotValue, Color color)
    {
        if (string.IsNullOrEmpty(slotValue)) return;
        if (!_byTarget.TryGetValue(slotValue, out var renderer)) return;
        StartCoroutine(FlashRoutine(renderer, color));
    }

    IEnumerator FlashRoutine(Renderer renderer, Color color)
    {
        if (renderer == null) yield break;

        var baseColor = _baseColors.TryGetValue(renderer, out var b) ? b : renderer.material.color;
        renderer.material.color = color;

        float elapsed = 0f;
        while (elapsed < flashSeconds)
        {
            elapsed += Time.deltaTime;
            renderer.material.color = Color.Lerp(color, baseColor, elapsed / flashSeconds);
            yield return null;
        }
        renderer.material.color = baseColor;
    }

    void AppendLog(string line)
    {
        if (commandLogText == null) return;

        _logLines.Enqueue($"[{Time.time:F1}s] {line}");
        while (_logLines.Count > maxLogLines)
            _logLines.Dequeue();

        _logBuilder.Length = 0;
        foreach (var entry in _logLines)
            _logBuilder.AppendLine(entry);

        commandLogText.text = _logBuilder.ToString();
    }

    void UpdateLastCommandPanel(VoxrCommand cmd)
    {
        if (lastCommandText == null) return;

        var sb = new StringBuilder(128);
        sb.Append("intent: ").AppendLine(cmd.Intent);
        sb.Append("score:  ").AppendFormat("{0:F2}", cmd.Score).AppendLine();

        if (cmd.Slots != null && cmd.Slots.Length > 0)
        {
            sb.AppendLine("slots:");
            foreach (var slot in cmd.Slots)
                sb.Append("  ").Append(slot.Name).Append(" = ").AppendLine(slot.Value);
        }
        else
        {
            sb.AppendLine("slots:  (none)");
        }

        lastCommandText.text = sb.ToString();
    }
}
