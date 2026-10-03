# Quiet Portable

A small Windows companion for ChatGPT, with a layered animated avatar, local read-aloud, smooth expressions, hair and body motion, accessories, and ambient particles.

**[Download Quiet Portable for Windows x64](https://github.com/Man7a/Quiet-Portable/releases/download/v2.8.0-preview/Quiet-Portable-2.8.0-preview-win-x64.zip)** · [Release notes and checksums](https://github.com/Man7a/Quiet-Portable/releases/tag/v2.8.0-preview)

This is a separate **2.8.0 preview edition**. It uses its own profile and can run alongside an installed Quiet. It does not import or replace an existing installation.

This release keeps the finished avatar's tuned physics, expressions and accessories.

## Get started

1. Extract the entire Windows x64 ZIP into a writable folder, such as `Documents\Quiet Portable`. Do not run from inside the ZIP or put it in Program Files.
2. Open **Start Quiet.cmd** or **QuietGPT.exe**.
3. Sign in to ChatGPT in Quiet. This profile is separate from your other browsers and installed Quiet.
4. Open the companion settings to adjust appearance, expressions, movement and particles. Your changes save automatically.

The .NET runtime is included. Microsoft Edge **WebView2 Evergreen Runtime** must be installed. It is usually present on Windows 10/11; if Quiet reports it missing, install it from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).

The avatar works without a speech model. Speech is optional and requires a supported **NVIDIA CUDA GPU and driver** in this edition. There is no CPU, Nano, Pocket TTS or Supertonic fallback.

Speech also needs Microsoft's **Visual C++ v14 x64 Runtime**. Setup checks for it before downloading the large packages. If it is missing, use Microsoft's [official installer](https://aka.ms/vc14/vc_redist.x64.exe), then retry voice setup. This separate Windows prerequisite may require administrator approval; Quiet's own downloads do not.

## Give her a voice

Open **Read aloud → Set up voice → Install voice**. Allow at least **12 GB of free space** for setup. Initial downloads include portable Python, CUDA/PyTorch packages and Chatterbox Turbo weights, so setup can take several minutes. Progress and errors appear in the setup window. Cancel stops its own installer process; downloaded cache files are kept for retry.

Setup downloads from GitHub, PyPI, PyTorch and Hugging Face. It creates a private Python runtime inside `VoiceRuntime`, with no administrator access, system Python installation or PATH changes. It verifies the bootstrap download and pinned CUDA wheels, downloads a fixed model revision, verifies the model files against that revision's hashes, then tests offline voice generation before marking speech ready. Downloads resume in chunks and retry interrupted requests.

Already have Turbo's model files? Choose **Use existing Turbo files…**, select their folder, then **Install voice**. Setup only reuses files whose size and checksum match the pinned revision; missing or different files are downloaded. Python and dependencies are still installed separately in Quiet's folder. No other application's runtime is modified.

After setup, choose **Test voice**, or close the setup window and press **Read**. The manual alternative is **Set up voice.cmd**; close Quiet before using that script to repair an existing runtime. Setup details from the in-app installer are saved in `VoiceRuntime\setup.log`.

Quiet ships with **F_Quiet.wav**, selected as her default reference voice. It is a small voice recording, not a bundled speech model; Turbo still needs the optional setup download. Under **Voice sample & tips**, choose another clean recording longer than five seconds to change her voice, or choose **Use Quiet's voice** to return to the included one. A WAV recording is recommended. Quiet copies custom recordings into its own `Data\Voices` folder so they move with the portable installation. Use recordings you have permission to use.

Choose the playback device under **Output device**. **Windows default** follows your current Windows output. You can also enable automatic reading of new replies, smiley expressions and occasional Turbo chuckles. Clicking the companion's lips reads the latest completed reply; clicking again stops it. Pause and Resume keep the same reply and audio clock.

Voice generation runs locally after setup. Models unload after the companion has been hidden and inactive for 30 seconds. ChatGPT itself still requires internet and a ChatGPT account; Quiet does not bypass subscription or service limits.

## Your files stay with Quiet

| Folder | Purpose |
| --- | --- |
| `Data` | ChatGPT WebView profile, settings, saved voice samples and optional bridge state |
| `VoiceRuntime` | Downloaded Python, packages, model weights, setup log and generated voice test |
| `Avatar` | Self-contained artwork and animation rig |
| `Voice` | Turbo worker and setup scripts |

Close Quiet completely before moving its entire folder. The Python runtime and installed packages avoid absolute virtual-environment paths. Voice sample preferences are stored relative to `Data`. A folder moved to another computer still needs compatible Windows, WebView2 and an NVIDIA driver for speech. Speaker devices and saved window placement may differ there.

Back up `Data` privately to keep your settings and login. **Never upload `Data` or `VoiceRuntime` to GitHub or include them in a release ZIP.** They can contain chat sessions, recordings, configuration and download logs. The included `.gitignore` excludes them. Updates should replace the application files only after Quiet closes, preserving those two folders.

## Troubleshooting

- **No voice installed:** the avatar continues silently. Open Read aloud and run setup.
- **GPU unavailable:** install a compatible NVIDIA driver. This preview does not provide a CPU voice fallback.
- **Setup failed or canceled:** retry; downloaded files are reused. Details are in the setup log. The avatar stays available.
- **No completed reply:** open a chat containing a finished text reply, or paste text into Read aloud. A reply still streaming is not read.
- **Missing speaker:** Quiet uses Windows default until the selected device reconnects.
- **Cannot save profile:** extract into a writable folder outside Program Files.
- **Moving or upgrading:** close Quiet and keep the whole folder together. Do not copy your old developer profile into the public ZIP.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run `build.ps1`. This produces a self-contained Windows x64 application in `build`. Voice dependencies remain an optional separate download. `package.ps1` creates the clean application and source ZIPs under `artifacts`, including the selected `F_Quiet.wav` voice sample and excluding profiles, downloaded models, custom recordings and diagnostic files.

For the native offline diagnostic suite, run `build.ps1 -Test -TestPython <path-to-python.exe>`. The test Python is only needed for the fake speech worker; it can be any available Python 3. Diagnostic profiles and screenshots live under `build\test-results`. No real chat is sent. Actual GPU voice setup and inference are separate tests.

The optional **Ray & Mira** bridge is an advanced Codex integration. It is disabled until you explicitly configure chat/task bindings. No personal chat bindings or workbench IDs are shipped.

## Release status

Public preview for download and testing. Runtime and packaging checks are documented in `VALIDATION.md`. A brand-new Windows machine has not yet been certified. No open-source license has been assigned to Quiet's own code, artwork or reference recording. Third-party projects keep their own licenses; see `THIRD-PARTY-NOTICES.md`.
