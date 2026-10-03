Quiet Portable's first public preview brings the finished companion into a separate Windows x64 folder, with its own settings and browser profile.

### Download and start

Download **Quiet-Portable-2.8.0-preview-win-x64.zip**, extract the entire folder somewhere writable, and open **Start Quiet.cmd**. The .NET runtime is included; Microsoft WebView2 is required. Sign in to ChatGPT within Quiet's separate browser profile. Existing Quiet installations are not replaced.

### Included

- The layered avatar with tuned motion, hair, breathing, face depth, blinking, expressions, accessories and ambient particles.
- Read-aloud controls, output-device selection, automatic reply reading and smiley-driven expressions.
- **F_Quiet.wav** as the default reference voice, with controls to choose a different sample or restore Quiet's voice.
- In-app **Set up voice** and the alternative **Set up voice.cmd**. Chatterbox Turbo, portable Python and runtime packages download separately; models are not included in the ZIP. Speech needs a compatible NVIDIA CUDA GPU/driver and Microsoft's Visual C++ v14 x64 Runtime. The avatar works without speech setup.
- A separate source archive, instructions, third-party notices and SHA-256 checksums.

### Preview status

40/40 native checks passed in an extracted copy without model downloads. Actual F_Quiet voice generation and folder relocation also passed on the development machine. Fresh-PC and broader GPU compatibility testing remain outstanding; mouth timing is approximate. See **VALIDATION.md** for the precise test scope.

No open-source license has been assigned to Quiet's own code, artwork or reference recording. Dependencies retain their individual licenses. Profiles, chats, downloaded models and personal settings are excluded from this release.
