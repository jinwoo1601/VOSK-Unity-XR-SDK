using UnityEngine;
using TMPro;
using VoskXR;

public class VoiceDemo : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] TextMeshProUGUI displayText;

    // The VoskSpeechRecogniser keeps its native model loaded between
    // OnDisable/OnEnable cycles so re-enabling is fast. Call
    // recogniser.ReleaseNativeResources() only when you want to fully
    // unload the model (e.g. OnDestroy).

    void OnEnable()
    {
        if (recogniser == null) return;
        recogniser.OnPartialResult += OnPartialResult;
        recogniser.OnFinalResult += OnFinalResult;
        recogniser.OnError += OnError;
        recogniser.StartRecognition();
    }

    void OnDisable()
    {
        if (recogniser == null) return;
        recogniser.OnPartialResult -= OnPartialResult;
        recogniser.OnFinalResult -= OnFinalResult;
        recogniser.OnError -= OnError;
        recogniser.StopRecognition();
    }

    void OnPartialResult(string text)
    {
        if (displayText != null)
            displayText.text = text;
    }

    void OnFinalResult(string text)
    {
        Debug.Log($"[VoiceDemo] Final: {text}");
        if (displayText != null)
            displayText.text = text;
    }

    void OnError(VoskBridgeErrorCode code, string message)
    {
        Debug.LogError($"[VoiceDemo] VOSK [{code}]: {message}");
    }
}
