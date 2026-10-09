using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenUtau.Headless;

/// <summary>
/// CPU WORLD controls, normalized to the same ranges as the desktop renderer.
/// </summary>
public sealed record WorldControls {
    /// <summary>
    /// Formant shift from zero through one; 0.5 is neutral.
    /// </summary>
    public double Timbre { get; init; } = .5;

    /// <summary>
    /// Spectral tension from zero through one; 0.5 is neutral.
    /// </summary>
    public double Tension { get; init; } = .5;

    /// <summary>
    /// Breathiness from zero through one; 0.5 is neutral.
    /// </summary>
    public double Breathiness { get; init; } = .5;

    /// <summary>
    /// Periodic component level from zero through one; one is full voicing.
    /// </summary>
    public double Voicing { get; init; } = 1;

    /// <summary>
    /// Output gain in decibels from -60 through 24; zero is neutral.
    /// </summary>
    public double GainDecibels { get; init; }
}

/// <summary>
/// Bounded CPU WORLD analysis cache and mono 44.1 kHz PCM16 synthesis from lazy streams.
/// </summary>
public sealed class WorldRenderer : IDisposable {
    private const int Rate = 44100, Hop = 220, Fft = 2048, Width = Fft / 2 + 1;
    private const double Period = Hop * 1000.0 / Rate;
    private readonly Voicebank bank;
    private readonly long cacheBudget;
    private readonly double maximumSourceSeconds;
    private readonly Dictionary<VoicebankSample, Features> cache = [];
    private readonly LinkedList<VoicebankSample> recent = [];
    private long cacheBytes;
    private readonly object sync = new();
    private static readonly object NativeGate = new();
    private bool disposed;
    private sealed record Features(double[] F0, double[] Spectrum, double[] Aperiodicity) {
        public long Bytes => (long)(F0.Length + Spectrum.Length + Aperiodicity.Length) * 8;
    }

    /// <summary>
    /// Creates a renderer without loading audio or starting any inference runtime.
    /// </summary>
    /// <param name="bank">Metadata-loaded voicebank.</param>
    /// <param name="maximumCacheBytes">Maximum retained WORLD analysis bytes; defaults to 64 MiB.</param>
    /// <param name="maximumSourceSeconds">Maximum decoded source duration, between 0.01 and 30 seconds.</param>
    public WorldRenderer(Voicebank bank, long maximumCacheBytes = 64 * 1024 * 1024, double maximumSourceSeconds = 30) {
        if (maximumCacheBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        if (!double.IsFinite(maximumSourceSeconds) || maximumSourceSeconds is < .01 or > 30) throw new ArgumentOutOfRangeException(nameof(maximumSourceSeconds));
        this.bank = bank; cacheBudget = maximumCacheBytes;
        this.maximumSourceSeconds = maximumSourceSeconds;
    }

    /// <summary>
    /// Measures the median voiced source pitch for an alias, in hertz.
    /// </summary>
    /// <param name="alias">Resolved voicebank alias.</param>
    /// <param name="midi">Pitch used for prefix-map selection.</param>
    /// <returns>Median voiced fundamental, or 220 Hz if the source is unvoiced.</returns>
    public double MeasurePitch(string alias, int midi = 60) {
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            var values = Analyze(bank.Find(alias, midi) ?? throw new InvalidDataException($"Missing alias {alias}.")).F0.Where(value => value > 0).Order().ToArray();
            return values.Length == 0 ? 220 : values[values.Length / 2];
        }
    }

    /// <summary>
    /// Renders a bounded window using continuous phrase synthesis from the timeline origin.
    /// </summary>
    /// <param name="segments">Ordered, nonoverlapping aliases on the phrase timeline.</param>
    /// <param name="startMilliseconds">Start of the requested output window.</param>
    /// <param name="durationMilliseconds">Window length, greater than zero and at most 30 seconds.</param>
    /// <param name="controls">Validated renderer controls.</param>
    /// <param name="pitch">Optional MIDI contour on the global millisecond timeline.</param>
    /// <param name="cancellation">Cancels between parameter and audio pages.</param>
    /// <returns>Mono 44.1 kHz PCM16 for precisely the requested window.</returns>
    public byte[] Render(IReadOnlyList<PhonemeSegment> segments, double startMilliseconds, double durationMilliseconds,
        WorldControls controls, Func<double, double>? pitch = null, CancellationToken cancellation = default) {
        if (!double.IsFinite(startMilliseconds) || startMilliseconds < 0 || !double.IsFinite(durationMilliseconds) || durationMilliseconds is <= 0 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        var first = checked((long)Math.Round(startMilliseconds * Rate / 1000));
        var result = new byte[checked((int)Math.Round(durationMilliseconds * Rate / 1000)) * 2];
        long position = 0;
        var totalDuration = Math.Max(startMilliseconds + durationMilliseconds, segments.Count == 0 ? 0 : segments[^1].StartMilliseconds + segments[^1].DurationMilliseconds + 100);
        foreach (var page in RenderPages(segments, totalDuration, controls, pitch, cancellation: cancellation)) {
            try {
                var begin = Math.Max(first, position);
                var end = Math.Min(first + result.Length / 2, position + page.Length / 2);
                if (begin < end) page.AsSpan(checked((int)(begin - position) * 2), checked((int)(end - begin) * 2))
                    .CopyTo(result.AsSpan(checked((int)(begin - first) * 2)));
                position += page.Length / 2;
                if (position >= first + result.Length / 2) break;
            } finally { Array.Clear(page); }
        }
        return result;
    }

    /// <summary>
    /// Synthesizes connected aliases with one WORLD pulse train and bounded parameter and PCM pages.
    /// </summary>
    /// <param name="segments">Ordered, nonoverlapping aliases; gaps remain rests.</param>
    /// <param name="durationMilliseconds">Output duration from the timeline origin, below WORLD's signed sample limit.</param>
    /// <param name="controls">Normalized voice controls and output gain.</param>
    /// <param name="pitch">Optional MIDI contour on the global millisecond timeline.</param>
    /// <param name="firstPageMilliseconds">First output page length, between 1 and 5000 milliseconds.</param>
    /// <param name="pageMilliseconds">Subsequent page length, between 1 and 5000 milliseconds.</param>
    /// <param name="cancellation">Cancels before source analysis and between audio blocks.</param>
    /// <returns>Caller-owned PCM16 pages; abandoning enumeration disposes native synthesis state.</returns>
    public IEnumerable<byte[]> RenderPages(IReadOnlyList<PhonemeSegment> segments, double durationMilliseconds,
        WorldControls controls, Func<double, double>? pitch = null, double firstPageMilliseconds = 250,
        double pageMilliseconds = 5000, CancellationToken cancellation = default) {
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds <= 0 || durationMilliseconds * Rate / 1000 >= int.MaxValue - Rate)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        foreach (var length in new[] { firstPageMilliseconds, pageMilliseconds })
            if (!double.IsFinite(length) || length is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(pageMilliseconds));
        foreach (var value in new[] { controls.Timbre, controls.Tension, controls.Breathiness, controls.Voicing })
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(controls));
        if (!double.IsFinite(controls.GainDecibels) || controls.GainDecibels is < -60 or > 24) throw new ArgumentOutOfRangeException(nameof(controls));
        double lastEnd = 0;
        foreach (var segment in segments) {
            if (!double.IsFinite(segment.StartMilliseconds) || segment.StartMilliseconds < lastEnd - 1e-6 ||
                !double.IsFinite(segment.DurationMilliseconds) || segment.DurationMilliseconds is <= 0 or > 30000 ||
                !double.IsFinite(segment.Midi) || segment.Midi is < 0 or > 127)
                throw new ArgumentException("Aliases must have finite nonoverlapping intervals and MIDI pitch.", nameof(segments));
            lastEnd = segment.StartMilliseconds + segment.DurationMilliseconds;
        }
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            nint stream;
            lock (NativeGate) { stream = Native.WorldStreamCreate(); }
            if (stream == 0) throw new InvalidOperationException("Unable to create continuous WORLD synthesis.");
            var samples = checked((int)Math.Round(durationMilliseconds * Rate / 1000));
            var frameLimit = checked((int)Math.Ceiling((durationMilliseconds + 100) / Period) + 1);
            var f0 = new double[10];
            var spectrum = new double[10 * Width];
            var aperiodicity = new double[spectrum.Length];
            var block = new double[512];
            var frame = 0;
            var emitted = 0;
            var segmentIndex = 0;
            var gain = Math.Pow(10, controls.GainDecibels / 20);
            byte[]? page = null;
            try {
                page = new byte[Math.Min(samples, (int)Math.Round(firstPageMilliseconds * Rate / 1000)) * 2];
                var filled = 0;
                while (emitted < samples) {
                    cancellation.ThrowIfCancellationRequested();
                    int count;
                    lock (NativeGate) { count = Native.WorldStreamRead(stream, block); }
                    if (count < 0) throw new InvalidOperationException("WORLD streaming synthesis failed.");
                    if (count == 0) {
                        if (frame >= frameLimit) throw new InvalidOperationException("WORLD did not flush the requested output duration.");
                        var frames = Math.Min(10, frameLimit - frame);
                        // At least two final frames are required by WORLD's interpolation.
                        if (frames == 1) frames = 2;
                        Array.Clear(spectrum); Array.Fill(aperiodicity, 1);
                        for (var index = 0; index < frames; index++, frame++) {
                            var time = frame * Period;
                            while (segmentIndex < segments.Count && segments[segmentIndex].StartMilliseconds + segments[segmentIndex].DurationMilliseconds <= time)
                                segmentIndex++;
                            f0[index] = 0;
                            spectrum.AsSpan(index * Width, Width).Fill(1e-16);
                            if (segmentIndex < segments.Count && time >= segments[segmentIndex].StartMilliseconds && time < durationMilliseconds) {
                                FillFrame(segments[segmentIndex], time, pitch, spectrum.AsSpan(index * Width, Width), aperiodicity.AsSpan(index * Width, Width), out f0[index]);
                                // Blend adjacent source envelopes, rather than fading each phoneme to silence.
                                var next = segmentIndex + 1;
                                if (next < segments.Count && Math.Abs(segments[next].StartMilliseconds - segments[segmentIndex].StartMilliseconds - segments[segmentIndex].DurationMilliseconds) < 1) {
                                    var sample = bank.Find(segments[next].Alias, (int)Math.Round(segments[next].Midi)) ?? throw new InvalidDataException($"Missing alias {segments[next].Alias}.");
                                    var blend = Math.Min(Math.Clamp(sample.Overlap, 5, 30), Math.Min(segments[segmentIndex].DurationMilliseconds, segments[next].DurationMilliseconds) / 4);
                                    var boundary = segments[next].StartMilliseconds;
                                    if (time > boundary - blend) {
                                        var sp = new double[Width]; var ap = new double[Width];
                                        FillFrame(segments[next], boundary, pitch, sp, ap, out var nextF0);
                                        var weight = (time - boundary + blend) / blend;
                                        for (var bin = 0; bin < Width; bin++) {
                                            spectrum[index * Width + bin] = spectrum[index * Width + bin] * (1 - weight) + sp[bin] * weight;
                                            aperiodicity[index * Width + bin] = aperiodicity[index * Width + bin] * (1 - weight) + ap[bin] * weight;
                                        }
                                        f0[index] = f0[index] > 0 && nextF0 > 0 ? f0[index] * (1 - weight) + nextF0 * weight :
                                            weight >= .5 ? nextF0 : f0[index];
                                    }
                                }
                            }
                        }
                        lock (NativeGate) {
                            if (Native.WorldStreamAdd(stream, f0, frames, spectrum, aperiodicity, controls.Timbre,
                                controls.Tension, controls.Breathiness, controls.Voicing) != 1)
                                throw new InvalidOperationException("WORLD parameter queue could not make progress.");
                        }
                        continue;
                    }
                    for (var index = 0; index < count && emitted < samples; index++, emitted++) {
                        // Taper only phrase edges; internal phoneme and PCM-page joins retain their energy and phase.
                        var taper = Math.Min(1, Math.Min(emitted, samples - 1 - emitted) / (Rate / 200.0));
                        var value = double.IsFinite(block[index]) ? Math.Clamp(block[index] * gain * taper, -1, 1) : 0;
                        BinaryPrimitives.WriteInt16LittleEndian(page!.AsSpan(filled, 2), (short)Math.Round(value * 32767));
                        filled += 2;
                        if (filled == page!.Length) {
                            var completed = page; page = null;
                            yield return completed;
                            filled = 0;
                            if (emitted + 1 < samples) page = new byte[Math.Min(samples - emitted - 1, (int)Math.Round(pageMilliseconds * Rate / 1000)) * 2];
                        }
                    }
                }
            } finally {
                lock (NativeGate) { Native.WorldStreamDestroy(stream); }
                if (page != null) Array.Clear(page);
                Array.Clear(block); Array.Clear(f0); Array.Clear(spectrum); Array.Clear(aperiodicity);
            }
        }
    }

    private void FillFrame(PhonemeSegment segment, double time, Func<double, double>? pitch, Span<double> spectrum, Span<double> aperiodicity, out double f0) {
        var sample = bank.Find(segment.Alias, (int)Math.Round(segment.Midi)) ?? throw new InvalidDataException($"Missing alias {segment.Alias}.");
        var source = Analyze(sample);
        var sourceDuration = (source.F0.Length - 1) * Period;
        var sourceFixed = Math.Clamp(Math.Max(sample.Consonant, sample.Preutterance), 0, sourceDuration);
        var outputFixed = Math.Min(sourceFixed, segment.DurationMilliseconds * .45);
        var offset = Math.Clamp(time - segment.StartMilliseconds, 0, segment.DurationMilliseconds);
        var sourceTime = offset < outputFixed ? offset * sourceFixed / outputFixed : sourceFixed +
            (offset - outputFixed) * (sourceDuration - sourceFixed) / Math.Max(1, segment.DurationMilliseconds - outputFixed);
        var position = Math.Clamp(sourceTime / Period, 0, source.F0.Length - 1);
        var left = (int)position; var right = Math.Min(left + 1, source.F0.Length - 1); var fraction = position - left;
        var midi = pitch?.Invoke(time) ?? segment.Midi;
        if (!double.IsFinite(midi) || midi is < 0 or > 127) throw new InvalidDataException("Pitch curve exceeds MIDI range.");
        f0 = source.F0[(int)Math.Round(position)] <= 0 ? 0 : 440 * Math.Pow(2, (midi - 69) / 12);
        for (var bin = 0; bin < Width; bin++) {
            spectrum[bin] = source.Spectrum[left * Width + bin] * (1 - fraction) + source.Spectrum[right * Width + bin] * fraction;
            aperiodicity[bin] = source.Aperiodicity[left * Width + bin] * (1 - fraction) + source.Aperiodicity[right * Width + bin] * fraction;
        }
    }

    private Features Analyze(VoicebankSample sample) {
        if (cache.TryGetValue(sample, out var existing)) { recent.Remove(sample); recent.AddLast(sample); return existing; }
        using var stream = bank.Resources.Open(sample.Resource);
        var samples = WaveSamples.Read(stream, Rate, maximumSourceSeconds);
        var start = Math.Clamp((int)Math.Round(Math.Max(0, sample.Offset) * Rate / 1000), 0, samples.Length);
        var end = sample.Cutoff < 0 ? start + (int)Math.Round(-sample.Cutoff * Rate / 1000) : samples.Length - (int)Math.Round(sample.Cutoff * Rate / 1000);
        end = Math.Clamp(end, start, samples.Length);
        if (end - start < Rate / 100) throw new InvalidDataException("OTO sample has no usable audio.");
        var trimmed = samples[start..end]; Array.Clear(samples);
        var frames = Native.F0FrameCount(trimmed.Length, Rate, Period, 0);
        var result = new Features(new double[frames], new double[checked(frames * Width)], new double[checked(frames * Width)]);
        try {
            lock (NativeGate) {
                Native.F0(trimmed, trimmed.Length, Rate, Period, 0, result.F0);
                Native.InitAnalysisConfig(out var config, Rate, Hop, Fft);
                Native.WorldAnalysisF0In(ref config, trimmed, trimmed.Length, result.F0, frames, result.Spectrum, result.Aperiodicity);
            }
        }
        finally { Array.Clear(trimmed); }
        if (result.Bytes <= cacheBudget) {
            while (cacheBytes + result.Bytes > cacheBudget && recent.First != null) {
                var victim = recent.First.Value; recent.RemoveFirst(); cacheBytes -= cache[victim].Bytes; cache.Remove(victim);
            }
            cache.Add(sample, result); recent.AddLast(sample); cacheBytes += result.Bytes;
        }
        return result;
    }

    /// <summary>
    /// Releases all cached source analysis; no resource streams remain open.
    /// </summary>
    public void Dispose() { lock (sync) { disposed = true; cache.Clear(); recent.Clear(); cacheBytes = 0; } }

    private static class Native {
        private const string Library = "openutau-worldcpu";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint WorldStreamCreate();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int WorldStreamAdd(nint stream, double[] f0, int frames, double[] sp, double[] ap, double timbre, double tension, double breathiness, double voicing);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int WorldStreamRead(nint stream, double[] output);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void WorldStreamDestroy(nint stream);
        [StructLayout(LayoutKind.Sequential)]
        internal struct Config { internal int Fs, Hop, Fft; internal float Floor; internal double Period; }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int F0FrameCount(int length, int fs, double period, int method);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int F0(float[] samples, int length, int fs, double period, int method, double[] f0);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void InitAnalysisConfig(out Config config, int fs, int hop, int fft);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void WorldAnalysisF0In(ref Config config, float[] samples, int length, double[] f0, int frames, double[] sp, double[] ap);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int WorldSynthesisSampleCount(int frames, double period, int fs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int WorldSynthesis(double[] f0, int frames, double[] sp, [MarshalAs(UnmanagedType.I1)] bool mgc, int mgcSize,
            double[] ap, [MarshalAs(UnmanagedType.I1)] bool bap, int fft, double period, int fs, double[] output, double[] gender, double[] tension, double[] breath, double[] voicing);
    }
}

internal static class WaveSamples {
    internal static float[] Read(Stream stream, int targetRate, double maximumSeconds) {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, true);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Voicebank sample must be RIFF WAV.");
        reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Voicebank sample must be WAVE.");
        ushort format = 0, channels = 0, bits = 0; uint rate = 0; byte[]? data = null;
        while (true) {
            uint id;
            try { id = reader.ReadUInt32(); } catch (EndOfStreamException) { break; }
            var length = reader.ReadUInt32();
            if (length > 32 * 1024 * 1024) throw new InvalidDataException("Voicebank WAV chunk exceeds 32 MiB.");
            var chunk = reader.ReadBytes((int)length);
            if (chunk.Length != length) throw new EndOfStreamException();
            if ((length & 1) != 0) reader.ReadByte();
            if (id == 0x20746d66) {
                if (length < 16) throw new InvalidDataException("Truncated WAV format.");
                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk); channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(2));
                rate = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)); bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(14));
            }
            else if (id == 0x61746164) { data = chunk; break; }
        }
        if (data == null || channels is 0 or > 8 || rate is < 8000 or > 192000 ||
            !((format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits == 32))) throw new InvalidDataException("Unsupported voicebank WAV format.");
        var stride = bits / 8 * channels;
        if (data.Length % stride != 0) throw new InvalidDataException("Incomplete WAV sample frame.");
        var sourceCount = data.Length / stride;
        if (sourceCount == 0 || sourceCount > rate * maximumSeconds) {
            Array.Clear(data);
            throw new InvalidDataException("Voicebank source exceeds the configured duration bound.");
        }
        var source = new float[sourceCount];
        for (var i = 0; i < sourceCount; i++) {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++) {
                var p = data.AsSpan(i * stride + channel * bits / 8);
                sum += format == 3 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p)) : bits switch {
                    8 => (p[0] - 128) / 128.0, 16 => BinaryPrimitives.ReadInt16LittleEndian(p) / 32768.0,
                    24 => ((p[0] | p[1] << 8 | p[2] << 16) << 8 >> 8) / 8388608.0, 32 => BinaryPrimitives.ReadInt32LittleEndian(p) / 2147483648.0, _ => 0 };
            }
            source[i] = double.IsFinite(sum) ? (float)(sum / channels) : 0;
        }
        Array.Clear(data);
        if (rate == targetRate) return source;
        var result = new float[checked((int)((long)sourceCount * targetRate / rate))];
        for (var i = 0; i < result.Length; i++) {
            var position = (double)i * rate / targetRate; var left = Math.Min((int)position, sourceCount - 1); var right = Math.Min(left + 1, sourceCount - 1);
            result[i] = (float)(source[left] * (1 - (position - left)) + source[right] * (position - left));
        }
        Array.Clear(source); return result;
    }
}
