namespace OpenUtau.Headless;

/// <summary>
/// Caller-supplied ARPAbet pronunciation; dictionary and G2P selection stay outside the renderer.
/// </summary>
public interface IEnglishPronunciations {
    /// <summary>
    /// Resolves a word into ARPAbet phonemes, retaining vowel stress suffixes.
    /// </summary>
    /// <param name="word">English word or proper name.</param>
    /// <param name="cancellation">Cancels pronunciation acquisition.</param>
    /// <returns>Nonempty pronunciation.</returns>
    ValueTask<string[]> Get(string word, CancellationToken cancellation = default);
}

/// <summary>
/// Timed syllable supplied to the English alias planner.
/// </summary>
public sealed record PhonemeNote {
    /// <summary>
    /// ARPAbet pronunciation including stress suffixes; no neural inference occurs in the planner.
    /// </summary>
    public required string[] Phonemes { get; init; }

    /// <summary>
    /// Onset on the phrase timeline in milliseconds.
    /// </summary>
    public double StartMilliseconds { get; init; }

    /// <summary>
    /// Positive note duration in milliseconds.
    /// </summary>
    public double DurationMilliseconds { get; init; }

    /// <summary>
    /// Nominal pitch in MIDI semitones; fractional values are supported by rendering.
    /// </summary>
    public double Midi { get; init; }
}

/// <summary>
/// Concrete voicebank alias and its bounded phrase interval.
/// </summary>
public sealed record PhonemeSegment(string Alias, double StartMilliseconds, double DurationMilliseconds, double Midi);

/// <summary>
/// English X-SAMPA CVVC planning using OpenUtau's established alias fallback rules.
/// </summary>
public sealed class EnglishPhonemizer {
    private readonly Voicebank bank;

    /// <summary>
    /// Creates an alias planner for a metadata-loaded voicebank.
    /// </summary>
    /// <param name="bank">Voicebank with pitch-aware oto aliases.</param>
    public EnglishPhonemizer(Voicebank bank) { this.bank = bank; }

    /// <summary>
    /// Plans syllable transitions and endings without loading audio samples.
    /// </summary>
    /// <param name="notes">Ordered nonoverlapping notes with supplied pronunciations.</param>
    /// <returns>Resolved voicebank alias segments on the same millisecond timeline.</returns>
    public IReadOnlyList<PhonemeSegment> Plan(IReadOnlyList<PhonemeNote> notes) {
        var rules = new EnglishXsampaRules((alias, midi) => bank.Find(alias, midi) != null);
        var result = new List<PhonemeSegment>();
        var previous = "";
        var pending = Array.Empty<string>();
        double end = 0;
        for (var n = 0; n < notes.Count; n++) {
            var note = notes[n];
            if (!double.IsFinite(note.StartMilliseconds) || !double.IsFinite(note.DurationMilliseconds) ||
                note.DurationMilliseconds <= 0 || note.StartMilliseconds < end || !double.IsFinite(note.Midi) || note.Midi is < 0 or > 127)
                throw new ArgumentException("Notes must have finite nonoverlapping timing and MIDI pitch.", nameof(notes));
            if (note.StartMilliseconds > end + 1) { previous = ""; pending = []; }
            var tone = (int)Math.Round(note.Midi);
            var phones = rules.Prepare(note.Phonemes, tone);
            var vowels = phones.Select((phone, index) => (phone, index)).Where(item => rules.IsVowel(item.phone)).ToArray();
            if (vowels.Length == 0) throw new InvalidDataException("A sounded note must contain a vowel.");
            var cursor = 0;
            var duration = note.DurationMilliseconds / vowels.Length;
            for (var v = 0; v < vowels.Length; v++) {
                var vowel = vowels[v];
                var consonants = pending.Concat(phones[cursor..vowel.index]).ToArray();
                var aliases = rules.ProcessSyllable(new EnglishXsampaRules.Syllable { prevV = previous, cc = consonants,
                    v = vowel.phone, tone = tone, vowelTone = tone, attr = [], vowelAttr = [], canAliasBeExtended = false,
                    prevWordConsonantsCount = pending.Length, nextV = "", nextCc = [] });
                var concrete = aliases.Where(alias => alias != null).ToArray();
                if (concrete.Length == 0) throw new InvalidDataException("Voicebank cannot realize this syllable.");
                var start = note.StartMilliseconds + v * duration;
                var transition = concrete.Length > 1 ? Math.Min(50, duration * .4 / (concrete.Length - 1)) : 0;
                for (var a = 0; a < concrete.Length; a++) {
                    var alias = rules.ValidateAlias(concrete[a], tone);
                    if (bank.Find(alias, tone) == null) throw new InvalidDataException($"Missing voicebank alias '{alias}'.");
                    var length = a == concrete.Length - 1 ? duration - transition * a : transition;
                    result.Add(new(alias, start + transition * a, length, note.Midi));
                }
                previous = vowel.phone; pending = []; cursor = vowel.index + 1;
            }
            pending = phones[cursor..];
            end = note.StartMilliseconds + note.DurationMilliseconds;
            if (n == notes.Count - 1 || notes[n + 1].StartMilliseconds > end + 1) {
                var ending = rules.ProcessEnding(new EnglishXsampaRules.Ending { prevV = previous, cc = pending, tail = "-", tone = tone, attr = [] });
                var endingAliases = ending.Where(alias => alias != null && bank.Find(alias, tone) != null).ToArray();
                if (endingAliases.Length > 0 && result.Count > 0) {
                    var last = result[^1];
                    var tailDuration = Math.Min(90, last.DurationMilliseconds * .25);
                    result[^1] = last with { DurationMilliseconds = last.DurationMilliseconds - tailDuration };
                    for (var e = 0; e < endingAliases.Length; e++)
                        result.Add(new(endingAliases[e], end - tailDuration + e * tailDuration / endingAliases.Length, tailDuration / endingAliases.Length, note.Midi));
                }
                previous = ""; pending = [];
            }
        }
        return result;
    }
}
