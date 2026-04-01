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
        recogniser.OnResult += OnResult;
        recogniser.OnError += OnError;
        recogniser.StartRecognition();
    }

    void OnDisable()
    {
        if (recogniser == null) return;
        recogniser.OnPartialResult -= OnPartialResult;
        recogniser.OnFinalResult -= OnFinalResult;
        recogniser.OnResult -= OnResult;
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

    void OnResult(VoskResult result)
    {
        if (result.Alternatives.Length > 0)
        {
            for (int i = 0; i < result.Alternatives.Length; i++)
            {
                var alt = result.Alternatives[i];
                Debug.Log($"[VoiceDemo] Alt {i}: \"{alt.Text}\" score={alt.Confidence:F1}");
            }
        }
        else
        {
            foreach (var word in result.Words)
                Debug.Log($"[VoiceDemo]   \"{word.Text}\" conf={word.Confidence:F2} " +
                          $"({word.StartTime:F2}s – {word.EndTime:F2}s)");
        }
    }

    void OnError(VoskBridgeErrorCode code, string message)
    {
        Debug.LogError($"[VoiceDemo] VOSK [{code}]: {message}");
    }
}
