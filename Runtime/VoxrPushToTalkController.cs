// ============================================================================
// Purpose:  MonoBehaviour gating speech recognition via hold-to-talk or continuous listening
// Layer:    Runtime
// Owns:     VoxrPushToTalkController (public MonoBehaviour)
// Depends:  VoxrSpeechRecogniser, VoxrCommandRecogniser, VoxrListeningMode
// ============================================================================
using UnityEngine;
using UnityEngine.Events;
using VoXR.Commands;

namespace VoXR
{
    [AddComponentMenu("VoXR/Push-To-Talk Controller")]
    public sealed class VoxrPushToTalkController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The speech recogniser to start and stop. Required.")]
        [SerializeField] VoxrSpeechRecogniser _speechRecogniser;

        [Tooltip("Optional command recogniser. When assigned, ReleaseTalk flushes the " +
                 "utterance buffer so speech spoken just before release parses immediately.")]
        [SerializeField] VoxrCommandRecogniser _commandRecogniser;

        [Header("Behaviour")]
        [Tooltip("PushToTalk gates recognition to a button press; Continuous runs recognition " +
                 "whenever the component is enabled. Can be changed at runtime via ListeningMode.")]
        [SerializeField] VoxrListeningMode _listeningMode = VoxrListeningMode.PushToTalk;

        [Tooltip("Call VoxrSpeechRecogniser.Initialise() in Start so the model is pre-warmed " +
                 "before the first PressTalk. Disable if your code calls Initialise() elsewhere.")]
        [SerializeField] bool _initialiseOnStart = true;

        [Tooltip("When enabled, ReleaseTalk also cancels any pending command awaiting " +
                 "confirmation or follow-up slot-fill on the command recogniser.")]
        [SerializeField] bool _cancelPendingOnRelease = false;

        [Header("Events")]
        [SerializeField] UnityEvent _onTalkStarted = new UnityEvent();
        [SerializeField] UnityEvent _onTalkEnded = new UnityEvent();

        bool _wantRecognising;

        public VoxrListeningMode ListeningMode
        {
            get => _listeningMode;
            set
            {
                if (_listeningMode == value)
                    return;

                var previous = _listeningMode;
                _listeningMode = value;

                if (value == VoxrListeningMode.Continuous)
                {
                    if (!_wantRecognising)
                    {
                        _wantRecognising = true;
                        _speechRecogniser?.StartRecognition();
                        _onTalkStarted?.Invoke();
                    }
                }
                else // PushToTalk
                {
                    if (previous == VoxrListeningMode.Continuous && _wantRecognising)
                    {
                        _wantRecognising = false;
                        _speechRecogniser?.StopRecognition();
                        _onTalkEnded?.Invoke();
                    }
                }
            }
        }

        public UnityEvent OnTalkStarted => _onTalkStarted;

        public UnityEvent OnTalkEnded => _onTalkEnded;

        public void PressTalk()
        {
            if (_listeningMode != VoxrListeningMode.PushToTalk) return;
            if (_speechRecogniser == null) return;
            if (_wantRecognising) return;

            _wantRecognising = true;
            _speechRecogniser.StartRecognition();
            _onTalkStarted?.Invoke();
        }

        public void ReleaseTalk()
        {
            if (_listeningMode != VoxrListeningMode.PushToTalk) return;
            if (_speechRecogniser == null) return;
            if (!_wantRecognising) return;

            _wantRecognising = false;
            _speechRecogniser.StopRecognition();

            if (_commandRecogniser != null)
            {
                _commandRecogniser.FlushPendingBuffer();
                if (_cancelPendingOnRelease)
                    _commandRecogniser.CancelPendingCommand();
            }

            _onTalkEnded?.Invoke();
        }

        void Awake()
        {
            if (_listeningMode == VoxrListeningMode.Continuous)
                _wantRecognising = true;
        }

        void Start()
        {
            if (_initialiseOnStart && _speechRecogniser != null)
                _speechRecogniser.Initialise();
        }

        void OnEnable()
        {
            if (_wantRecognising && _speechRecogniser != null)
                _speechRecogniser.StartRecognition();
        }

        void OnDisable()
        {
            if (_wantRecognising && _speechRecogniser != null)
                _speechRecogniser.StopRecognition();
        }

        void OnApplicationPause(bool paused)
        {
            if (_speechRecogniser == null) return;

            if (paused)
            {
                if (_wantRecognising)
                    _speechRecogniser.StopRecognition();
            }
            else
            {
                if (_wantRecognising && !_speechRecogniser.IsRecognising)
                    _speechRecogniser.StartRecognition();
            }
        }

        void Update()
        {
            // Closes the Android mic-permission race: the permission coroutine can fire
            // a native start after the user already released, so reconcile on the next frame.
            if (!_wantRecognising && _speechRecogniser != null && _speechRecogniser.IsRecognising)
                _speechRecogniser.StopRecognition();
        }

        internal VoxrSpeechRecogniser SpeechRecogniser { set => _speechRecogniser = value; }
        internal VoxrCommandRecogniser CommandRecogniser { set => _commandRecogniser = value; }
        internal bool InitialiseOnStart { set => _initialiseOnStart = value; }
        internal bool CancelPendingOnRelease { set => _cancelPendingOnRelease = value; }
    }
}
