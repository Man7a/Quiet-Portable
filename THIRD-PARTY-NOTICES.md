# Third-party notices

Quiet's own code, logo and avatar artwork do not yet have a public redistribution license selected. Do not infer a license for those assets from the projects listed below.

`Voice/F_Quiet.wav` is the reference recording selected by the owner for this edition. It is included in the application and source archives, separately from Chatterbox's model weights. No license for that recording is implied by Chatterbox's license.

The self-contained Windows distribution includes Microsoft .NET runtime components. Their packaged `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` are retained. [.NET](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) uses the MIT license, with additional component notices.

Microsoft Windows SDK .NET assemblies and WebView2 SDK are distributed under their Microsoft package terms. See `licenses` for the accompanying package terms, and [WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47). WebView2 Evergreen Runtime is a separate Microsoft prerequisite.

Voice setup downloads these projects separately, rather than bundling model weights or Python packages in the release:

- [uv](https://github.com/astral-sh/uv/tree/0.9.5): MIT / Apache-2.0; bootstrap archive version 0.9.5, SHA-256 verified.
- [Python](https://docs.python.org/3.11/license.html): Python Software Foundation license. Managed Python 3.11.14 comes from [Astral python-build-standalone](https://github.com/astral-sh/python-build-standalone).
- [Chatterbox](https://github.com/resemble-ai/chatterbox/blob/5de7a54aa4e5e2baadb0182dde554908b48b85c2/LICENSE): MIT. Source commit `5de7a54aa4e5e2baadb0182dde554908b48b85c2`; Turbo model revision `749d1c1a46eb10492095d68fbcf55691ccf137cd` from [ResembleAI/chatterbox-turbo](https://huggingface.co/ResembleAI/chatterbox-turbo). Its watermarking is retained.
- [PyTorch](https://github.com/pytorch/pytorch/blob/v2.6.0/LICENSE) and [torchaudio](https://github.com/pytorch/audio/blob/v2.6.0/LICENSE): BSD-style licenses, with bundled CUDA/component terms. CUDA 12.4 wheels are pinned and hashed. No separate CUDA Toolkit install is required.
- [PocketSphinx](https://github.com/cmusphinx/pocketsphinx/blob/master/LICENSE): BSD-style license; used for local English speech-sound timing, not as another speech voice model.

Downloaded packages retain their `.dist-info` license files and model downloads retain their supplied license/readme files. Transitive dependencies have their own licenses. `Voice/requirements-turbo.txt` specifies runtime inputs; `VoiceRuntime` is local generated data and must be omitted from release artifacts.
