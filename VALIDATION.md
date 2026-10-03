# Quiet Portable preview validation

Edition: **2.8.0-preview**, Windows x64. Local validation date: **2026-10-03**.

## What was checked

- Self-contained .NET 10 Release build; a separate application directory, profile and instance mutex. No installed Quiet files are replaced.
- **40/40 native offline diagnostics passed in the extracted release directory with no downloaded speech runtime.** An isolated WebView profile and local ChatGPT-shaped fixtures cover avatar loading, accepted workshop shapes and physics, expressions, particles, settings, reply collection, emoji timing, read-aloud gestures, pause/resume, output-device routing and bridge safeguards. These fixtures do not send messages to ChatGPT or Codex. A test's fixed prompt-length allowance was corrected to measure message overhead separately from the portable folder path.
- Actual Chatterbox Turbo generation on an NVIDIA GeForce RTX 4090 using the included **F_Quiet.wav** reference. Setup produced a 2.64-second voice test with 29 aligned mouth cues and no timing warnings. Native diagnostics also exercise actual voice generation and playback.
- The selected reference is included as `Voice/F_Quiet.wav` (516,174 bytes, 5.85 seconds, mono PCM). Its SHA-256 is `f88c0e74370b07c8214212829f00c1288c084805aa0988fb7c608c38e10bc353`. New profiles select it by default. Custom samples are copied into `Data/Voices`; the included default is saved with a relocatable bundled-reference marker. The read-aloud panel provides **Use Quiet's voice** to restore it.
- **41/41 native checks passed with actual Turbo speech enabled** in the prepared runtime. **The separate relocation check also passed** after moving the portable folder to a path containing spaces, including its downloaded Python runtime, saved bundled/default voice selection, custom sample, avatar, actual F_Quiet inference and mouth timing.
- Five network-free download tests: interrupted transfer/resume, corrupt cached file, verified cache reuse, checksum rejection and incorrect server range rejection. Run with the installed voice packages available on `PYTHONPATH` (including `certifi`).
- Live downloads of `ve.safetensors` and `vocab.json` through the final downloader, checked against the pinned Hugging Face revision's LFS SHA-256 and Git-blob SHA-1 values respectively.
- Twenty source/privacy and baseline integrity checks: no personal filesystem paths or chat bindings in the shipped application sources; old embedded speech recordings replaced with silent animation fixtures; accepted chest physics code identical to the installed edition; installed Quiet executable, assembly, avatar and worker hashes unchanged.
- Native diagnostics verify avatar loading, tuned ring geometry, secondary motion and whole-chest shading.
- Curated release packaging excludes `Data`, `VoiceRuntime`, profiles, logs, diagnostic output, development caches and PSD sources. The owner-selected F_Quiet reference is intentionally included. Archive checks verify its bytes and the presence of the application, avatar and setup scripts.

## Limits of this preview

Validation used the current Windows machine with WebView2, Visual C++ x64 Runtime and an NVIDIA driver already installed. It is not certification on a fresh Windows installation or every GPU. CUDA speech is optional; the avatar is available without model downloads.

The full voice setup was exercised in a separate local runtime. Large model files already available locally were reused only after checksum validation. The final ranged downloader was tested with real smaller model files and simulated interruptions; a completely fresh download of all multi-gigabyte weights through that final downloader has not been repeated.

GPU voice generation and alignment succeed; visual mouth timing is approximate, and voice output quality varies with the reference and text. Dependency deprecation/reference-length messages may appear in setup details without causing setup failure.

Published as a GitHub preview with the owner's authorization. No open-source license has been assigned to Quiet's own code, artwork or reference recording. See `THIRD-PARTY-NOTICES.md` for separate dependency terms.
