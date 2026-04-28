using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VoXR;

public class VoiceDemo : MonoBehaviour
{
    [SerializeField] VoxrSpeechRecogniser recogniser;

    [Header("UI (optional)")]
    [SerializeField] Text transcriptText;
    [SerializeField] Text wordsText;
    [SerializeField] Text errorText;

    [Tooltip("How long the error toast stays visible after the last error fires.")]
    [SerializeField] float errorVisibleSeconds = 4f;

    readonly StringBuilder _wordsBuilder = new StringBuilder(256);
    float _errorClearAt;

    // The VoxrSpeechRecogniser keeps its native model loaded between
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

        if (errorText != null) errorText.enabled = false;
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

    void Update()
    {
        if (errorText != null && errorText.enabled && Time.unscaledTime >= _errorClearAt)
            errorText.enabled = false;
    }

    void OnPartialResult(string text)
    {
        if (transcriptText != null)
            transcriptText.text = string.IsNullOrEmpty(text) ? "<listening...>" : text;
    }

    void OnFinalResult(string text)
    {
        Debug.Log($"[VoiceDemo] Final: {text}");
        if (transcriptText != null)
            transcriptText.text = string.IsNullOrEmpty(text) ? "<silence>" : text;
    }

    void OnResult(VoxrResult result)
    {
        UpdateWordsPanel(result);

        foreach (var word in result.Words)
            Debug.Log($"[VoiceDemo]   \"{word.Text}\" conf={word.Confidence:F2} " +
                      $"({word.StartTime:F2}s - {word.EndTime:F2}s)");
    }

    void UpdateWordsPanel(VoxrResult result)
    {
        if (wordsText == null) return;

        _wordsBuilder.Length = 0;
        if (result.Words.Length == 0)
        {
            wordsText.text = "<no words>";
            return;
        }

        foreach (var word in result.Words)
            _wordsBuilder.AppendFormat("{0,-14} {1:F2}  {2:F2}-{3:F2}s\n",
                word.Text, word.Confidence, word.StartTime, word.EndTime);

        wordsText.text = _wordsBuilder.ToString();
    }

    void OnError(VoxrBridgeErrorCode code, string message)
    {
        Debug.LogError($"[VoiceDemo] VOSK [{code}]: {message}");

        if (errorText != null)
        {
            errorText.text = $"[{code}] {message}";
            errorText.enabled = true;
            _errorClearAt = Time.unscaledTime + errorVisibleSeconds;
        }
    }
}
