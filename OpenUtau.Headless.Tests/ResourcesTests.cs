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

    private static StreamVoicebankResources BankResources(List<string> opened) => new(["oto.ini", "i.wav"], name => {
        opened.Add(name);
        return new MemoryStream(name == "oto.ini" ? Encoding.UTF8.GetBytes("i.wav=- bi,12,50,-200,40,10\ni.wav=i,0,50,0,0,0\ni.wav=i -,0,50,0,0,0") : [1, 2, 3]);
    });
}
