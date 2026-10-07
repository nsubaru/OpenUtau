# Headless voicebank rendering

This library loads UTAU metadata and WAVs through `IVoicebankResources`. Desktop callers can use `FileVoicebankResources`; applications with mapped, archived, or remotely authorized assets can supply `StreamVoicebankResources`. Each stream belongs to its caller and is closed after metadata loading or sample analysis. Resource names are relative to an immutable bank inventory.

`EnglishPhonemizer` accepts ARPAbet pronunciations with stress and uses the existing English X-SAMPA alias fallback rules. Pronunciation acquisition can implement `IEnglishPronunciations`; this library includes no dictionary downloads, ONNX inference, or model runtime. `PhonemeNote` timing is in milliseconds and pitch is in MIDI semitones.

`WorldRenderer` lazily analyzes the source WAV for each resolved alias and renders bounded windows as mono 44.1 kHz little-endian PCM16. Its default source-analysis cache is limited to 64 MiB. Caller-provided pitch contours and normalized timbre, tension, breathiness, and voicing controls apply to CPU WORLD synthesis. Samples stay outside temporary output buffers, and no external resampler process is launched.

Build the managed library with `dotnet build OpenUtau.Headless/OpenUtau.Headless.csproj`. Build `openutau-worldcpu` with CMake using this directory as the source. WORLD and spline revisions and archive hashes match the existing native build; their existing patches are applied during dependency preparation. This target excludes desktop UI, neural renderers, ONNX, pyin, and debug NumPy output. Place the resulting native library beside the calling application.

Run metadata and pronunciation tests with `dotnet test --project OpenUtau.Headless.Tests/OpenUtau.Headless.Tests.csproj`. Filesystem support for the desktop applications is unchanged.
