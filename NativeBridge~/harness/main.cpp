// ============================================================================
// Purpose:  WSL WAV->transcript harness — replays the fixture corpus through
//           the desktop bridge in push mode and compares final transcripts
//           against the committed expectation baseline (Tier C)
// Layer:    NativeBridge.Harness
// Owns:     (executable entry point, no public types)
// Depends:  WavData, LoadWav, vosk_bridge C ABI, RingBuffer
// ============================================================================

#include "vosk_bridge.h"
#include "ring_buffer.h"
#include "wav_reader.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

using nlohmann::json;

namespace {

struct FixtureOutcome {
    std::string file;
    std::vector<std::string> expected;
    std::vector<std::string> actual;
    std::vector<std::string> errors;
    bool pass = false;
};

// Pops everything currently queued. Result strings are copied immediately —
// the pointer from vosk_bridge_get_result dies on the next call. Non-final
// {"error": ...} entries (ring overflow, device error) fail the run loudly.
void DrainResults(FixtureOutcome& r) {
    int is_final = 0, length = 0;
    const char* p = nullptr;
    while ((p = vosk_bridge_get_result(&is_final, &length)) != nullptr) {
        std::string payload(p, static_cast<size_t>(length));
        json j = json::parse(payload, nullptr, false);
        if (j.is_discarded()) {
            r.errors.push_back("unparseable result: " + payload);
            continue;
        }
        if (j.contains("error")) {
            r.errors.push_back(payload);
            continue;
        }
        if (is_final) {
            std::string text = j.value("text", "");
            if (!text.empty())
                r.actual.push_back(text);
        }
    }
}

// Pushes the whole buffer with clamped-write pacing: a short write means the
// ring is full, so drain results and back off briefly. The stall guard is a
// liveness backstop only — verdicts never depend on timing.
bool PushAll(const float* data, size_t count, FixtureOutcome& r) {
    size_t offset = 0;
    int stalled = 0;
    while (offset < count) {
        uint32_t chunk = static_cast<uint32_t>(std::min<size_t>(16384, count - offset));
        int written = vosk_bridge_push_audio(data + offset, chunk);
        if (written < 0) {
            r.errors.push_back("vosk_bridge_push_audio returned error " +
                               std::to_string(written));
            return false;
        }
        offset += static_cast<size_t>(written);
        if (static_cast<uint32_t>(written) < chunk) {
            if (written == 0 && ++stalled > 2000) { // ~10 s without progress
                r.errors.push_back("recognition thread stalled (ring never drained)");
                return false;
            }
            if (written > 0)
                stalled = 0;
            DrainResults(r);
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        } else {
            stalled = 0;
        }
    }
    return true;
}

int Fail(const std::string& msg) {
    std::fprintf(stderr, "vosk-bridge-harness: %s\n", msg.c_str());
    return 2;
}

} // namespace

int main(int argc, char** argv) {
    std::string model_dir, fixtures_dir, manifest_path;
    bool write_baseline = false;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--model" && i + 1 < argc) model_dir = argv[++i];
        else if (arg == "--fixtures" && i + 1 < argc) fixtures_dir = argv[++i];
        else if (arg == "--manifest" && i + 1 < argc) manifest_path = argv[++i];
        else if (arg == "--write-baseline") write_baseline = true;
        else return Fail("unknown or incomplete argument: " + arg + "\nusage: vosk-bridge-harness --model <dir> --fixtures <dir> --manifest <expectations.json> [--write-baseline]");
    }
    if (model_dir.empty() || fixtures_dir.empty() || manifest_path.empty())
        return Fail("--model, --fixtures and --manifest are all required");

    std::ifstream mf(manifest_path);
    if (!mf)
        return Fail("cannot open manifest: " + manifest_path);
    json manifest = json::parse(mf, nullptr, false);
    if (manifest.is_discarded())
        return Fail("manifest is not valid JSON: " + manifest_path);
    if (!manifest.contains("cases") || !manifest["cases"].is_array())
        return Fail("manifest has no 'cases' array: " + manifest_path);

    float gain = manifest.value("gain", -18.0f);

    int rc = vosk_bridge_init(model_dir.c_str(), 16000.0f, gain);
    if (rc != VOSK_BRIDGE_OK)
        return Fail("vosk_bridge_init failed with code " + std::to_string(rc) +
                    " (model: " + model_dir + ")");

    if (manifest.contains("grammar") && manifest["grammar"].is_array() &&
        !manifest["grammar"].empty()) {
        std::string grammar = manifest["grammar"].dump();
        rc = vosk_bridge_set_grammar(grammar.c_str());
        if (rc != VOSK_BRIDGE_OK) {
            vosk_bridge_destroy();
            return Fail("vosk_bridge_set_grammar failed with code " + std::to_string(rc));
        }
    } else {
        std::fprintf(stderr, "vosk-bridge-harness: warning: no grammar in manifest — free decode\n");
    }

    // Padding pushed after each fixture: 1 s of silence lets endpointing fire
    // in sample time, and a further ring-capacity's worth guarantees every
    // fixture sample was consumed before stop (consumed >= pushed - capacity).
    const std::vector<float> padding(48000 + RingBuffer<float>::kCapacity, 0.0f);

    std::vector<FixtureOutcome> outcomes;
    bool run_error = false;

    for (auto& c : manifest["cases"]) {
        FixtureOutcome r;
        r.file = c.value("file", "");
        if (c.contains("expectedFinals") && c["expectedFinals"].is_array())
            for (auto& e : c["expectedFinals"])
                r.expected.push_back(e.get<std::string>());

        WavData wav;
        std::string wav_err = LoadWav(fixtures_dir + "/" + r.file, wav);
        if (!wav_err.empty()) {
            r.errors.push_back(wav_err);
        } else {
            rc = vosk_bridge_start_push();
            if (rc != VOSK_BRIDGE_OK) {
                r.errors.push_back("vosk_bridge_start_push failed with code " +
                                   std::to_string(rc));
            } else {
                if (PushAll(wav.samples.data(), wav.samples.size(), r))
                    PushAll(padding.data(), padding.size(), r);
                DrainResults(r);
                vosk_bridge_stop();
                DrainResults(r); // the tail final is flushed during stop's join
                vosk_bridge_reset();
            }
        }

        r.pass = r.errors.empty() && (write_baseline || r.actual == r.expected);
        if (!r.errors.empty())
            run_error = true;
        if (write_baseline)
            c["expectedFinals"] = r.actual;
        outcomes.push_back(std::move(r));
    }

    vosk_bridge_destroy();

    if (write_baseline && !run_error) {
        std::ofstream out(manifest_path);
        if (!out)
            return Fail("cannot write manifest: " + manifest_path);
        out << manifest.dump(4) << "\n";
    }

    json report;
    report["cases"] = json::array();
    int passed = 0;
    for (auto& r : outcomes) {
        report["cases"].push_back({{"file", r.file},
                                   {"pass", r.pass},
                                   {"expected", r.expected},
                                   {"actual", r.actual},
                                   {"errors", r.errors}});
        if (r.pass)
            ++passed;
    }
    report["passed"] = passed;
    report["failed"] = static_cast<int>(outcomes.size()) - passed;
    report["total"] = static_cast<int>(outcomes.size());
    report["baselineWritten"] = write_baseline && !run_error;
    std::printf("%s\n", report.dump(2).c_str());

    if (run_error)
        return 2;
    return passed == static_cast<int>(outcomes.size()) ? 0 : 1;
}
