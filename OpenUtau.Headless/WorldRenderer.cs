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
    /// Renders a bounded window of supplied aliases, with optional continuous MIDI pitch.
    /// </summary>
    /// <param name="segments">Resolved phonemes on the phrase timeline.</param>
    /// <param name="startMilliseconds">Start of the requested output window.</param>
    /// <param name="durationMilliseconds">Window length, greater than zero and at most 30 seconds.</param>
    /// <param name="controls">Validated renderer controls.</param>
    /// <param name="pitch">Optional MIDI contour evaluated on the global millisecond timeline.</param>
    /// <param name="cancellation">Cancels between phoneme renders.</param>
    /// <returns>Mono 44.1 kHz little-endian PCM16 for precisely the requested window.</returns>
    public byte[] Render(IReadOnlyList<PhonemeSegment> segments, double startMilliseconds, double durationMilliseconds,
        WorldControls controls, Func<double, double>? pitch = null, CancellationToken cancellation = default) {
        if (!double.IsFinite(startMilliseconds) || startMilliseconds < 0 || !double.IsFinite(durationMilliseconds) || durationMilliseconds is <= 0 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        foreach (var value in new[] { controls.Timbre, controls.Tension, controls.Breathiness, controls.Voicing })
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(controls));
        if (!double.IsFinite(controls.GainDecibels) || controls.GainDecibels is < -60 or > 24) throw new ArgumentOutOfRangeException(nameof(controls));
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            var output = new double[checked((int)Math.Round(durationMilliseconds * Rate / 1000))];
            try {
                foreach (var segment in segments) {
                    cancellation.ThrowIfCancellationRequested();
                    if (segment.StartMilliseconds + segment.DurationMilliseconds <= startMilliseconds || segment.StartMilliseconds >= startMilliseconds + durationMilliseconds) continue;
                    if (segment.DurationMilliseconds is <= 0 or > 30000) throw new InvalidDataException("Phoneme duration exceeds a bounded render window.");
                    var sample = bank.Find(segment.Alias, (int)Math.Round(segment.Midi)) ?? throw new InvalidDataException($"Missing alias {segment.Alias}.");
                    var source = Analyze(sample);
                    var frames = Math.Max(2, checked((int)Math.Ceiling(segment.DurationMilliseconds / Period)) + 1);
                    var f0 = new double[frames];
                    var sp = new double[checked(frames * Width)];
                    var ap = new double[sp.Length];
                    var gender = Enumerable.Repeat(controls.Timbre, frames).ToArray();
                    var tension = Enumerable.Repeat(controls.Tension, frames).ToArray();
                    var breath = Enumerable.Repeat(controls.Breathiness, frames).ToArray();
                    var voicing = Enumerable.Repeat(controls.Voicing, frames).ToArray();
                    var sourceDuration = (source.F0.Length - 1) * Period;
                    var fixedDuration = Math.Clamp(sample.Consonant, 0, Math.Min(sourceDuration * .8, segment.DurationMilliseconds * .5));
                    for (var frame = 0; frame < frames; frame++) {
                        var time = Math.Min(segment.DurationMilliseconds, frame * Period);
                        var sourceTime = time < fixedDuration ? time : fixedDuration +
                            (time - fixedDuration) * (sourceDuration - fixedDuration) / Math.Max(1, segment.DurationMilliseconds - fixedDuration);
                        var position = Math.Clamp(sourceTime / Period, 0, source.F0.Length - 1);
                        var left = (int)position; var right = Math.Min(left + 1, source.F0.Length - 1); var fraction = position - left;
                        var midi = pitch?.Invoke(segment.StartMilliseconds + time) ?? segment.Midi;
                        if (!double.IsFinite(midi) || midi is < 0 or > 127) throw new InvalidDataException("Pitch curve exceeds MIDI range.");
                        f0[frame] = source.F0[left] <= 0 ? 0 : 440 * Math.Pow(2, (midi - 69) / 12);
                        for (var bin = 0; bin < Width; bin++) {
                            sp[frame * Width + bin] = source.Spectrum[left * Width + bin] * (1 - fraction) + source.Spectrum[right * Width + bin] * fraction;
                            ap[frame * Width + bin] = source.Aperiodicity[left * Width + bin] * (1 - fraction) + source.Aperiodicity[right * Width + bin] * fraction;
                        }
                    }
                    var rendered = new double[Native.WorldSynthesisSampleCount(frames, Period, Rate)];
                    Native.WorldSynthesis(f0, frames, sp, false, 0, ap, false, Fft, Period, Rate, rendered, gender, tension, breath, voicing);
                    var offset = checked((int)Math.Round((segment.StartMilliseconds - startMilliseconds) * Rate / 1000));
                    var length = Math.Min(rendered.Length, checked((int)Math.Round(segment.DurationMilliseconds * Rate / 1000)));
                    // Short tapers remove discontinuities without changing supplied note timing.
                    var fade = Math.Min(length / 4, Rate / 200);
                    for (var i = Math.Max(0, -offset); i < length && offset + i < output.Length; i++) {
                        var taper = fade == 0 ? 1 : Math.Min(1, Math.Min((double)i / fade, (double)(length - 1 - i) / fade));
                        output[offset + i] += rendered[i] * taper;
                    }
                }
                var bytes = new byte[output.Length * 2]; var gain = Math.Pow(10, controls.GainDecibels / 20);
                for (var i = 0; i < output.Length; i++) {
                    var value = double.IsFinite(output[i]) ? Math.Clamp(output[i] * gain, -1, 1) : 0;
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), (short)Math.Round(value * 32767));
                }
                return bytes;
            }
            finally { Array.Clear(output); }
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
            Native.F0(trimmed, trimmed.Length, Rate, Period, 0, result.F0);
            Native.InitAnalysisConfig(out var config, Rate, Hop, Fft);
            Native.WorldAnalysisF0In(ref config, trimmed, trimmed.Length, result.F0, frames, result.Spectrum, result.Aperiodicity);
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
