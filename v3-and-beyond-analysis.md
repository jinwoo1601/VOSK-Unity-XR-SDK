# v3+ Roadmap Analysis

v2.x covers the full lifecycle of **one-shot command recognition**: parse, score, buffer, extract, group, author. By v2.5, the command parsing layer is essentially complete. Below are the natural next themes.

---

## v3 — Editor Simulation & Iteration Speed

**The biggest pain point right now.** Every change requires a Quest deploy to test. This version would make the SDK usable in the Unity Editor.

- **Desktop audio backend** — Use Unity's `Microphone` class (or WASAPI via a small native plugin) to capture audio in-Editor. The existing pipeline (downsample -> VOSK) works on any platform; it's only the audio capture that's Android-specific.
- **x86_64 VOSK library** — Ship a Windows/macOS `libvosk` alongside the arm64 one. VOSK already publishes desktop builds. The bridge would need a thin platform abstraction (AAudio on Android, Unity Microphone on desktop).
- **Editor play-mode workflow** — Speak into your headset/desktop mic, see commands fire in the Console. Eliminates the deploy-test-logcat loop entirely.
- **Text injection API** — `commandRecogniser.InjectText("launch all missiles target hotel one")` for automated testing and CI. Bypasses audio entirely, feeds text directly to the parser. Trivial to implement, enormous for test coverage.
- **Simulated confidence** — Text injection could accept optional per-word confidence values so threshold logic can be tested without a mic.

This is probably the highest-impact version. It would cut iteration time from minutes to seconds.

---

## v4 — Dialogue & Contextual State

v2.x is stateless — each utterance is parsed independently. v4 introduces state that persists across utterances.

- **Context-dependent slots** — Slot values that change based on game state. A callback/delegate on `VoskSlotDefinition` that returns current valid values. Example: available targets come from a live game query, not a static list. Grammar regeneration on slot change (with the stop-set-start pattern from v2.4).
- **Follow-up commands** — After "launch missiles", the system enters a short-lived context where "target hotel one" alone is valid (completing the prior incomplete command). This extends the v2.3 utterance buffer concept into a stateful dialogue turn.
- **Confirmation flow** — High-stakes commands trigger `OnCommandPending` -> game shows "Confirm launch missiles?" -> player says "confirm" / "cancel". Already noted in the v2.x future ideas, but fleshed out with timeout, cancel vocabulary, and visual feedback hooks.
- **Pronoun/anaphora resolution** — "Launch missiles at hotel one. Fire torpedoes at it." -> "it" resolves to "hotel one" from prior command context. Scoped to simple last-target tracking, not full NLU.

---

## v5 — Feedback, Polish & Production Readiness

The "make it ship-quality" version.

- **Partial result preview** — Parse VOSK partial results to drive a real-time "command HUD" showing what the system thinks you're saying. `OnPartialCommand(VoskCommand candidate)` event. Needs flicker suppression (don't update faster than N hz, require stability window).
- **Audio/haptic feedback hooks** — Events for `OnListeningStarted`, `OnListeningStopped`, `OnCommandAccepted`, `OnCommandRejected`. The SDK doesn't play sounds itself, but provides the hooks so games can wire up acknowledgment beeps, controller haptics, etc.
- **Recognition analytics** — Lightweight stats: commands/minute, rejection rate, most-confused pairs, average confidence. Exposed as a struct you can query or log. Helps developers tune grammars and thresholds empirically rather than by gut feel.
- **Graceful degradation** — What happens when the mic permission is revoked mid-session? Model fails to load? Audio device disconnects? v1 has error codes but no recovery paths. This version adds retry logic, fallback states, and clear developer-facing guidance.
- **Per-user voice calibration** — A short calibration flow ("say these five phrases") that adjusts AGC target, confidence thresholds, or even VOSK speaker adaptation. Stored in PlayerPrefs or a save file.

---

## v6 — Platform Expansion & Multi-Language

- **iOS support** — AVAudioSession capture backend. VOSK supports iOS. The main work is a new audio capture implementation and Xcode build pipeline.
- **Standalone PC** — Already partially solved by v3's desktop backend, but formalized with proper builds, tested on SteamVR / desktop VR.
- **Multi-language** — `VoskSpeechRecogniser.SetModel(path)` to swap VOSK models at runtime. Language-tagged slot definitions. Grammar generation per-language. The hard part isn't code — it's that command patterns are language-specific ("launch missiles" vs "lancer les missiles"), so the developer needs per-language command definitions. The SDK provides the plumbing.
- **Model management** — Download models on-demand from a CDN instead of bundling in StreamingAssets (which bloats APK size). Progress callbacks, caching, integrity checks.

---

## Standalone Features (Could Slot Into Any Version)

| Feature | Notes |
|---------|-------|
| **Wake word / prefix routing** | Formalize `VoskCommandSet.Prefix` that auto-prepends to patterns. "Helm, set heading two seven zero" vs "Weapons, launch missiles". Works today manually but a first-class field would auto-manage grammar and provide cleaner semantics. |
| **Free-text / wildcard slots** | `VoskSlotType.FreeText` — captures all remaining tokens until next literal or end. Needs free-speech mode. Useful for "log note [anything]" or "name target [anything]". |
| **Command macros** | Developer-defined macro that expands one spoken command into multiple `OnCommandRecognised` events. "Battle stations" -> cease fire + set distance CQB + launch missiles. Pure C# sugar on top of existing events. |
| **Multiplayer voice routing** | In shared-space XR, attribute commands to specific players by spatial position or voice print. Heavy lift, probably out of scope for this SDK. |

---

## Recommended Priority

1. **v3 (Editor sim + text injection)** — Removes the #1 friction point. Everything after this ships faster because iteration is faster.
2. **v2.2-v2.5** — Finish the planned v2.x roadmap; it's well-designed and scoped.
3. **v4 (Dialogue/context)** — The most impactful gameplay feature after the basics.
4. **v5 (Polish)** — Makes the SDK production-grade.
5. **v6 (Platforms)** — Expands the market but is the most work for the least new capability.

---

## Version Summary (Full Roadmap)

| Version | Theme | Key Additions |
|---------|-------|---------------|
| **v1.0** | Speech Recognition | Offline VOSK on Quest, audio capture, AGC, alternatives |
| **v2.0** | Command Parsing | Pattern matching, grammar, shared slots, free-speech toggle |
| **v2.1** | Robustness | Scored matching, sliding start, aliases, optional literals, thresholds |
| **v2.2** | Numeric | NumberSequence slots, VoskNumberParser |
| **v2.3** | Continuity | Utterance buffer, sequential extraction, debounce |
| **v2.4** | Command Sets | Named command groups, runtime switching, push-to-talk |
| **v2.5** | Inspector Authoring | ScriptableObject slots/commands/sets, zero-code setup |
| **v3** | Editor Simulation | Desktop audio, text injection, in-Editor testing |
| **v4** | Dialogue & Context | Context-dependent slots, follow-ups, confirmation, anaphora |
| **v5** | Production Polish | Partial preview, analytics, feedback hooks, calibration |
| **v6** | Platform Expansion | iOS, standalone PC, multi-language, model management |
