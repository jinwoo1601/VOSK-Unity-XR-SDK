using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VoskXR;

public class PushToTalkDemo : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskPushToTalkController controller;
    [SerializeField] TextMeshProUGUI transcriptText;
    [SerializeField] Image recordingIndicator;
    [SerializeField] Color idleColor = new Color(0.2f, 0.2f, 0.2f, 1f);
    [SerializeField] Color activeColor = new Color(0.9f, 0.2f, 0.2f, 1f);

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

    public void TogglePushToTalkMode()
    {
        if (controller == null) return;

        controller.ListeningMode = controller.ListeningMode == VoskListeningMode.PushToTalk
            ? VoskListeningMode.Continuous
            : VoskListeningMode.PushToTalk;

        Debug.Log($"[PushToTalkDemo] Listening mode: {controller.ListeningMode}");
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
