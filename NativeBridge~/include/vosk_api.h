// vosk_api.h — VOSK C API declarations
// Copied from the upstream vosk-api repository (Apache 2.0).
// This header declares the functions exported by libvosk.so.
// Do not modify — it must match the prebuilt libvosk binary.

#ifndef VOSK_API_H
#define VOSK_API_H

#ifdef __cplusplus
extern "C" {
#endif

typedef struct VoskModel VoskModel;
typedef struct VoskSpkModel VoskSpkModel;
typedef struct VoskRecognizer VoskRecognizer;

// Model

VoskModel *vosk_model_new(const char *model_path);
void vosk_model_free(VoskModel *model);
int vosk_model_find_word(VoskModel *model, const char *word);

// Recognizer

VoskRecognizer *vosk_recognizer_new(VoskModel *model, float sample_rate);
VoskRecognizer *vosk_recognizer_new_spk(VoskModel *model, float sample_rate, VoskSpkModel *spk_model);
VoskRecognizer *vosk_recognizer_new_grm(VoskModel *model, float sample_rate, const char *grammar);

void vosk_recognizer_set_max_alternatives(VoskRecognizer *recognizer, int max_alternatives);
void vosk_recognizer_set_words(VoskRecognizer *recognizer, int words);
void vosk_recognizer_set_partial_words(VoskRecognizer *recognizer, int partial_words);
void vosk_recognizer_set_nlsml(VoskRecognizer *recognizer, int nlsml);
void vosk_recognizer_set_endpointer_mode(VoskRecognizer *recognizer, int mode);
void vosk_recognizer_set_endpointer_delays(VoskRecognizer *recognizer,
    float t_start_max, float t_end, float t_max);

int vosk_recognizer_accept_waveform(VoskRecognizer *recognizer, const char *data, int length);
int vosk_recognizer_accept_waveform_s(VoskRecognizer *recognizer, const short *data, int length);
int vosk_recognizer_accept_waveform_f(VoskRecognizer *recognizer, const float *data, int length);

const char *vosk_recognizer_result(VoskRecognizer *recognizer);
const char *vosk_recognizer_partial_result(VoskRecognizer *recognizer);
const char *vosk_recognizer_final_result(VoskRecognizer *recognizer);

void vosk_recognizer_reset(VoskRecognizer *recognizer);
void vosk_recognizer_free(VoskRecognizer *recognizer);

// Misc

void vosk_set_log_level(int log_level);
void vosk_gpu_init(void);
void vosk_gpu_thread_init(void);

#ifdef __cplusplus
}
#endif

#endif // VOSK_API_H
