using UnityEngine;
using UnityEngine.UI;
using VoskXR;

public class PushToTalkDemo : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskPushToTalkController controller;

    [Header("UI (optional)")]
    [SerializeField] Text transcriptText;
    [SerializeField] Text modeLabel;
    [SerializeField] Image recordingIndicator;

    [Header("Indicator Colours")]
    [SerializeField] Color idleColor = new Color(0.2f, 0.2f, 0.2f, 1f);
    [SerializeField] Color activeColor = new Color(0.9f, 0.2f, 0.2f, 1f);

    [Header("Keyboard")]
    [Tooltip("Hold this key to talk while in PushToTalk mode.")]
    [SerializeField] KeyCode talkKey = KeyCode.Space;
    [Tooltip("Press this key to toggle between PushToTalk and Continuous.")]
    [SerializeField] KeyCode toggleModeKey = KeyCode.Tab;

    void OnEnable()
    {
        if (recogniser != null)
        {
            recogniser.OnPartialResult += OnPartialResult;
            recogniser.OnFinalResult += OnFinalResult;
            recogniser.OnError += OnError;
        }

        if (controller != null)
        {
            controller.OnTalkStarted.AddListener(ShowRecording);
            controller.OnTalkEnded.AddListener(ShowIdle);
        }

        ShowIdle();
        RefreshModeLabel();
    }

    void OnDisable()
    {
        if (recogniser != null)
        {
            recogniser.OnPartialResult -= OnPartialResult;
            recogniser.OnFinalResult -= OnFinalResult;
            recogniser.OnError -= OnError;
        }

        if (controller != null)
        {
            controller.OnTalkStarted.RemoveListener(ShowRecording);
            controller.OnTalkEnded.RemoveListener(ShowIdle);
        }
    }

    void Update()
    {
        if (controller == null) return;

        if (controller.ListeningMode == VoskListeningMode.PushToTalk)
        {
            if (Input.GetKeyDown(talkKey))
                controller.PressTalk();
            if (Input.GetKeyUp(talkKey))
                controller.ReleaseTalk();
        }

        if (Input.GetKeyDown(toggleModeKey))
            TogglePushToTalkMode();
    }

    public void TogglePushToTalkMode()
    {
        if (controller == null) return;

        controller.ListeningMode = controller.ListeningMode == VoskListeningMode.PushToTalk
            ? VoskListeningMode.Continuous
            : VoskListeningMode.PushToTalk;

        Debug.Log($"[PushToTalkDemo] Listening mode: {controller.ListeningMode}");
        RefreshModeLabel();
    }

    public void OnHoldButtonPointerDown()
    {
        if (controller != null) controller.PressTalk();
    }

    public void OnHoldButtonPointerUp()
    {
        if (controller != null) controller.ReleaseTalk();
    }

    void RefreshModeLabel()
    {
        if (modeLabel == null || controller == null) return;
        modeLabel.text = controller.ListeningMode == VoskListeningMode.PushToTalk
            ? "Mode: Push-to-Talk (press Tab for Continuous)"
            : "Mode: Continuous (press Tab for Push-to-Talk)";
    }

    void ShowRecording()
    {
        if (recordingIndicator != null)
            recordingIndicator.color = activeColor;
    }

    void ShowIdle()
    {
        if (recordingIndicator != null)
            recordingIndicator.color = idleColor;
    }

    void OnPartialResult(string text)
    {
        if (transcriptText != null)
            transcriptText.text = text;
    }

    void OnFinalResult(string text)
    {
        if (transcriptText != null)
            transcriptText.text = text;
        Debug.Log($"[PushToTalkDemo] Final: {text}");
    }

    void OnError(VoskBridgeErrorCode code, string message)
    {
        Debug.LogError($"[PushToTalkDemo] VOSK [{code}] {code.ToDescription()}: {message}");
    }
}
