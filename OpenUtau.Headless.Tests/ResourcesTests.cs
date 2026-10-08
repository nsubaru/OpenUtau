using System.Text;
using Xunit;

namespace OpenUtau.Headless.Tests;

public class ResourcesTests {
    [Theory]
    [InlineData("../sample.wav")]
    [InlineData("/sample.wav")]
    [InlineData("dir/../sample.wav")]
    [InlineData("dir\\sample.wav")]
    public void ResourceInventoryRejectsEscapingNames(string name) {
        Assert.Throws<ArgumentException>(() => new StreamVoicebankResources([name], _ => new MemoryStream()));
    }

    [Fact]
    public void MetadataDoesNotAcquireSamplesAndPreservesTiming() {
        var opened = new List<string>();
        var resources = BankResources(opened);
        var bank = new Voicebank(resources, Encoding.UTF8);
        Assert.Equal(["oto.ini"], opened);
        var sample = bank.Find("- bi", 60)!;
        Assert.Equal("i.wav", sample.Resource);
        Assert.Equal(12, sample.Offset);
        Assert.Equal(50, sample.Consonant);
        Assert.Equal(-200, sample.Cutoff);
        Assert.Equal(40, sample.Preutterance);
        Assert.Equal(10, sample.Overlap);
        using (resources.Open("i.wav")) { }
        Assert.Equal(["oto.ini", "i.wav"], opened);
    }

    [Fact]
    public void SuppliedPronunciationPlansOnlyAvailableAliases() {
        var bank = new Voicebank(BankResources([]), Encoding.UTF8);
        var segments = new EnglishPhonemizer(bank).Plan([new() { Phonemes = ["B", "IY1"], DurationMilliseconds = 500, Midi = 60 }]);
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.NotNull(bank.Find(segment.Alias, 60)));
        Assert.Equal(0, segments[0].StartMilliseconds);
        Assert.Equal(500, segments[^1].StartMilliseconds + segments[^1].DurationMilliseconds);
    }

    [Fact]
    public void OverlappingNotesAndVowellessNotesAreRejected() {
        var planner = new EnglishPhonemizer(new Voicebank(BankResources([]), Encoding.UTF8));
        Assert.Throws<InvalidDataException>(() => planner.Plan([new() { Phonemes = ["B"], DurationMilliseconds = 500, Midi = 60 }]));
        Assert.Throws<ArgumentException>(() => planner.Plan([
            new() { Phonemes = ["IY1"], DurationMilliseconds = 500, Midi = 60 },
            new() { Phonemes = ["IY1"], StartMilliseconds = 400, DurationMilliseconds = 500, Midi = 60 }]));
    }

    [Fact]
    public void MissingSampleIsRejectedBeforeRendering() {
        var resources = new StreamVoicebankResources(["oto.ini"], _ => new MemoryStream("missing.wav=i,0,0,0,0,0"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => new Voicebank(resources, Encoding.UTF8));
    }

    [Fact]
    public void SourceDurationIsBoundedBeforeWorldAnalysis() {
        var pcm = new byte[44100 * 2];
        using var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, Encoding.ASCII, true)) {
            writer.Write(0x46464952u); writer.Write(36 + pcm.Length); writer.Write(0x45564157u);
            writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(44100u); writer.Write(88200u); writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write(0x61746164u); writer.Write(pcm.Length); writer.Write(pcm);
        }
        var resources = new StreamVoicebankResources(["oto.ini", "i.wav"], name =>
            new MemoryStream(name == "oto.ini" ? "i.wav=i,0,0,0,0,0"u8.ToArray() : wave.ToArray()));
        using var renderer = new WorldRenderer(new Voicebank(resources, Encoding.UTF8), maximumSourceSeconds: .1);
        Assert.Throws<InvalidDataException>(() => renderer.MeasurePitch("i"));
    }

    private static StreamVoicebankResources BankResources(List<string> opened) => new(["oto.ini", "i.wav"], name => {
        opened.Add(name);
        return new MemoryStream(name == "oto.ini" ? Encoding.UTF8.GetBytes("i.wav=- bi,12,50,-200,40,10\ni.wav=i,0,50,0,0,0\ni.wav=i -,0,50,0,0,0") : [1, 2, 3]);
    });
}
