# Headless voicebank rendering

This library loads UTAU metadata and WAVs through `IVoicebankResources`. Desktop callers can use `FileVoicebankResources`; applications with mapped, archived, or remotely authorized assets can supply `StreamVoicebankResources`. Each stream belongs to its caller and is closed after metadata loading or sample analysis. Resource names are relative to an immutable bank inventory.

`EnglishPhonemizer` accepts ARPAbet pronunciations with stress and uses the existing English X-SAMPA alias fallback rules. Pronunciation acquisition can implement `IEnglishPronunciations`; this library includes no dictionary downloads, ONNX inference, or model runtime. `PhonemeNote` timing is in milliseconds and pitch is in MIDI semitones.

Syllable allocation reserves time for consonant clusters, including the final coda. Ending aliases are resolved through the same bank-specific fallback rules as onsets and marked with `PhonemeSegment.IsEnding`. The renderer begins those sources at their OTO preutterance anchor so a short release does not replay and compress the leading vowel. Endings remain inside their supplied note interval; rests and caller-provided score timing are preserved.

`WorldRenderer` lazily analyzes the source WAV for each resolved alias. `RenderPages` synthesizes the connected phrase with one WORLD pulse train and overlap-add state, returning bounded mono 44.1 kHz little-endian PCM16 pages. PCM page boundaries do not restart synthesis. Adjacent aliases blend their spectral envelopes using bounded OTO overlap timing, and source consonant/preutterance regions are scaled separately from sustained regions. Explicit gaps remain rests. Fade-in and fade-out apply only at phrase edges. The renderer retains at most four short native parameter pages, independently of output length. Dispose the enumerator to cancel or abandon a render.

`Render` remains available for a bounded window; it advances continuous synthesis from the timeline origin and copies only that window. Use `RenderPages` for sequential playback or long output rather than repeatedly recomputing windows. The default source-analysis cache is limited to 64 MiB. Caller-provided pitch contours and normalized timbre, tension, breathiness, and voicing controls apply to CPU WORLD synthesis. Samples stay outside temporary output buffers, and no external resampler process is launched.

Build the managed library with `dotnet build OpenUtau.Headless/OpenUtau.Headless.csproj`. Build `openutau-worldcpu` with CMake using this directory as the source. WORLD and spline revisions and archive hashes match the existing native build; their existing patches are applied during dependency preparation. This target excludes desktop UI, neural renderers, ONNX, pyin, and debug NumPy output. Place the resulting native library beside the calling application.

Run metadata and pronunciation tests with `dotnet test --project OpenUtau.Headless.Tests/OpenUtau.Headless.Tests.csproj`. Filesystem support for the desktop applications is unchanged.

Set `OPENUTAU_WORLD_LIBRARY` to the full path of the built CPU WORLD library to additionally run acoustic continuity, PCM page partitioning, window equivalence, rest, and cancellation checks. Those synthesis checks explicitly skip when the library path is absent.
