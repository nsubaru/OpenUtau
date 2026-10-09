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
    public void FinalConsonantsHaveTimeToSoundWithoutConsumingTheRest() {
        var opened = new List<string>();
        var resources = new StreamVoicebankResources(["oto.ini", "i.wav"], name => {
            opened.Add(name);
            return new MemoryStream(Encoding.UTF8.GetBytes("i.wav=- bi,0,50,-400,40,10\ni.wav=i,0,50,-400,0,0\ni.wav=i -,0,300,-500,250,20\ni.wav=i d-,0,300,-500,250,20"));
        });
        var planner = new EnglishPhonemizer(new Voicebank(resources, Encoding.UTF8));
        var segments = planner.Plan([
            new() { Phonemes = ["B", "IY1", "D"], DurationMilliseconds = 500, Midi = 60 },
            new() { Phonemes = ["B", "IY1"], StartMilliseconds = 580, DurationMilliseconds = 500, Midi = 60 }]);
        var ending = Assert.Single(segments.Where(segment => segment.Alias == "i d-"));
        Assert.True(ending.IsEnding);
        Assert.True(ending.DurationMilliseconds >= 100);
        Assert.Equal(500, ending.StartMilliseconds + ending.DurationMilliseconds);
        Assert.Equal(580, segments.First(segment => segment.StartMilliseconds >= 500).StartMilliseconds);
        Assert.Equal(["oto.ini"], opened);
    }

    [Fact]
    public void FinalClustersKeepTheirTransitionsWithinTheSuppliedNote() {
        var resources = new StreamVoicebankResources(["oto.ini", "i.wav"], _ => new MemoryStream(Encoding.UTF8.GetBytes(
            "i.wav=- bi,0,50,-400,40,10\ni.wav=i,0,50,-400,0,0\ni.wav=i s,0,300,-400,250,20\ni.wav=s t-,0,300,-500,150,20")));
        var segments = new EnglishPhonemizer(new Voicebank(resources, Encoding.UTF8)).Plan([
            new() { Phonemes = ["B", "IY1", "S", "T"], DurationMilliseconds = 600, Midi = 60 }]);
        var endings = segments.Where(segment => segment.IsEnding).ToArray();
        Assert.Equal(["i s", "s t-"], endings.Select(segment => segment.Alias));
        Assert.All(endings, segment => Assert.True(segment.DurationMilliseconds >= 90));
        Assert.Equal(600, segments[^1].StartMilliseconds + segments[^1].DurationMilliseconds);
    }

    [Fact]
    public void EndingAliasesUseTheSameBankFallbackAsOnsets() {
        var resources = new StreamVoicebankResources(["oto.ini", "v.wav"], _ => new MemoryStream(Encoding.UTF8.GetBytes(
            "v.wav=- V,0,50,-400,0,0\nv.wav=V,0,50,-400,0,0\nv.wav=@ t-,0,300,-500,250,20")));
        var segments = new EnglishPhonemizer(new Voicebank(resources, Encoding.UTF8)).Plan([
            new() { Phonemes = ["AH1", "T"], DurationMilliseconds = 400, Midi = 60 }]);
        var ending = Assert.Single(segments.Where(segment => segment.IsEnding));
        Assert.Equal("@ t-", ending.Alias);
        Assert.Equal(400, ending.StartMilliseconds + ending.DurationMilliseconds);
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
