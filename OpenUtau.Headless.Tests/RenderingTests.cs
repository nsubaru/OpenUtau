using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace OpenUtau.Headless.Tests;

[CollectionDefinition("WORLD", DisableParallelization = true)]
public class WorldCollection { }

[Collection("WORLD")]
public class RenderingTests {
    private static bool loaded;

    public RenderingTests() {
        var path = Environment.GetEnvironmentVariable("OPENUTAU_WORLD_LIBRARY");
        if (string.IsNullOrEmpty(path)) Assert.Skip("Set OPENUTAU_WORLD_LIBRARY to the built CPU WORLD library to run synthesis checks.");
        if (!loaded) {
            NativeLibrary.SetDllImportResolver(typeof(WorldRenderer).Assembly, (name, _, _) => name == "openutau-worldcpu" ? NativeLibrary.Load(path!) : 0);
            loaded = true;
        }
    }

    [Fact]
    public void PcmPageSizeDoesNotChangeContinuousAudio() {
        using var renderer = Renderer();
        var segments = Segments();
        var large = renderer.RenderPages(segments, 1100, new(), firstPageMilliseconds: 250, pageMilliseconds: 5000).SelectMany(page => page).ToArray();
        var small = renderer.RenderPages(segments, 1100, new(), firstPageMilliseconds: 17, pageMilliseconds: 39).SelectMany(page => page).ToArray();
        Assert.Equal(1100 * 44100 / 1000 * 2, large.Length);
        Assert.Equal(large, small);
        var window = renderer.Render(segments, 233, 377, new());
        Assert.Equal(large.AsSpan((int)Math.Round(233 * 44.1) * 2, window.Length).ToArray(), window);
    }

    [Fact]
    public void ConnectedAliasesKeepEnergyWhileExplicitRestsRemainSilent() {
        using var renderer = Renderer();
        renderer.MeasurePitch("a");
        var pcm = renderer.RenderPages(Segments(), 1100, new()).SelectMany(page => page).ToArray();
        var samples = Enumerable.Range(0, pcm.Length / 2).Select(index => (double)BitConverter.ToInt16(pcm, index * 2)).ToArray();
        double Rms(double start, double duration) => Math.Sqrt(samples.Skip((int)Math.Round(start * 44.1)).Take((int)Math.Round(duration * 44.1)).Average(value => value * value));
        Assert.True(Rms(100, 40) > 100);
        Assert.True(Rms(297, 6) > Rms(260, 20) * .4, "Adjacent vowel aliases must not taper to silence at their shared boundary.");
        Assert.True(Rms(650, 80) < 2, "An explicit rest must not be filled by coarticulation.");
    }

    [Fact]
    public void CancellationAndAbandonedEnumerationReleaseSynthesisState() {
        using var renderer = Renderer();
        using var cancellation = new CancellationTokenSource();
        using (var pages = renderer.RenderPages(Segments(), 1100, new(), cancellation: cancellation.Token).GetEnumerator()) {
            Assert.True(pages.MoveNext());
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => pages.MoveNext());
        }
        using (var pages = renderer.RenderPages(Segments(), 1100, new()).GetEnumerator()) Assert.True(pages.MoveNext());
        Assert.NotEmpty(renderer.RenderPages(Segments(), 1100, new()).SelectMany(page => page));
    }

    [Fact]
    public void NormalizedControlsAndFiniteTimelineAreValidated() {
        using var renderer = Renderer();
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderPages(Segments(), 1100, new() { Timbre = double.NaN }).ToArray());
        Assert.Throws<ArgumentException>(() => renderer.RenderPages([new("a", 0, 500, 57), new("a", 400, 100, 57)], 1100, new()).ToArray());
        Assert.Throws<ArgumentException>(() => renderer.RenderPages([new("a", 0, 500, double.NaN)], 1100, new()).ToArray());
    }

    private static PhonemeSegment[] Segments() => [new("a", 0, 300, 57), new("a", 300, 300, 57), new("a", 800, 200, 57)];

    private static WorldRenderer Renderer() {
        using var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, Encoding.ASCII, true)) {
            const int count = 44100;
            writer.Write(0x46464952u); writer.Write(36 + count * 2); writer.Write(0x45564157u);
            writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(44100u); writer.Write(88200u); writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write(0x61746164u); writer.Write(count * 2);
            for (var index = 0; index < count; index++) writer.Write((short)(12000 * Math.Sin(index * 2 * Math.PI * 220 / 44100)));
        }
        var bytes = wave.ToArray();
        var resources = new StreamVoicebankResources(["oto.ini", "a.wav"], name => new MemoryStream(name == "oto.ini" ? "a.wav=a,100,0,-800,0,10"u8.ToArray() : bytes));
        return new WorldRenderer(new Voicebank(resources, Encoding.UTF8));
    }
}
