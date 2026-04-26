# Third-Party Notices

This package includes or depends on the following third-party software.

---

## VOSK Speech Recognition Toolkit

- **Project:** https://github.com/alphacep/vosk-api
- **Author:** Alpha Cephei Inc. (https://alphacephei.com/)
- **License:** Apache License 2.0
- **Usage:** This package bundles prebuilt VOSK native libraries for offline speech recognition. `libvosk.so` for Android arm64 ships under `Runtime/Plugins/Android/arm64-v8a/`. `libvosk.dll` for Windows x86_64 -- together with its three MinGW runtime dependencies (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`, `libwinpthread-1.dll`) -- ships under `Runtime/Plugins/x86_64/` for the Editor live-microphone backend. Sources for both are available at the [VOSK releases](https://github.com/alphacep/vosk-api/releases) page.
- **Models:** VOSK language models (e.g. `vosk-model-small-en-us-0.15`) are downloaded separately by the developer and are not included in this package. Models are provided by Alpha Cephei under the Apache 2.0 license.

### Apache License 2.0

```
Copyright 2019-2024 Alpha Cephei Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

---

## Kaldi

- **Project:** https://github.com/kaldi-asr/kaldi
- **Author:** Kaldi contributors
- **License:** Apache License 2.0
- **Usage:** VOSK is built on top of the Kaldi speech recognition toolkit. Kaldi components are compiled into the VOSK native libraries.

---

## OpenFST

- **Project:** https://www.openfst.org/
- **Author:** Google Inc. and contributors
- **License:** Apache License 2.0
- **Usage:** Used by Kaldi/VOSK for finite-state transducer operations in the speech decoder.
