using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace OpenUtau.Headless;

/// <summary>
/// Immutable voicebank inventory with lazy, independently owned resource streams.
/// </summary>
public interface IVoicebankResources {
    /// <summary>
    /// Canonical slash-separated names relative to the voicebank root.
    /// </summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>
    /// Opens a resource for this reader; disposing the stream releases its backing lease.
    /// </summary>
    /// <param name="name">Exact inventory name.</param>
    /// <returns>Readable stream positioned at its beginning.</returns>
    Stream Open(string name);
}

/// <summary>
/// Desktop filesystem implementation of the same resource contract used by mapped banks.
/// </summary>
public sealed class FileVoicebankResources : IVoicebankResources {
    private readonly string root;
    private readonly FrozenDictionary<string, string> files;

    /// <summary>
    /// Snapshots the filenames beneath an existing voicebank directory.
    /// </summary>
    /// <param name="directory">Voicebank root.</param>
    public FileVoicebankResources(string directory) {
        root = Path.GetFullPath(directory);
        files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .ToFrozenDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), path => path, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => files.Keys;

    /// <inheritdoc />
    public Stream Open(string name) => File.OpenRead(files.TryGetValue(name, out var path) ? path : throw new FileNotFoundException(name));
}

/// <summary>
/// Stream factory implementation; resource ownership and authorization remain with the caller.
/// </summary>
public sealed class StreamVoicebankResources : IVoicebankResources {
    private readonly FrozenSet<string> names;
    private readonly Func<string, Stream> open;

    /// <summary>
    /// Creates a fixed resource inventory without acquiring WAV contents.
    /// </summary>
    /// <param name="names">Unique canonical resource names.</param>
    /// <param name="open">Factory returning a new readable stream for each reader.</param>
    public StreamVoicebankResources(IEnumerable<string> names, Func<string, Stream> open) {
        this.names = names.ToFrozenSet(StringComparer.Ordinal);
        if (this.names.Any(name => name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(part => part is ".." or "." or "")))
            throw new ArgumentException("Resource names must remain relative to the voicebank root.", nameof(names));
        this.open = open ?? throw new ArgumentNullException(nameof(open));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => names;

    /// <inheritdoc />
    public Stream Open(string name) => names.Contains(name) ? open(name) : throw new FileNotFoundException(name);
}

/// <summary>
/// Sample alias and UTAU timing in milliseconds, resolved without opening its WAV.
/// </summary>
public sealed record VoicebankSample {
    /// <summary>
    /// Canonical resource name for the sample WAV.
    /// </summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Alias as recorded in oto.ini.
    /// </summary>
    public required string Alias { get; init; }

    /// <summary>
    /// Leading source offset in milliseconds.
    /// </summary>
    public double Offset { get; init; }

    /// <summary>
    /// Fixed consonant region in milliseconds after the source offset.
    /// </summary>
    public double Consonant { get; init; }

    /// <summary>
    /// Trim from the WAV end when positive, or usable duration after offset when negative.
    /// </summary>
    public double Cutoff { get; init; }

    /// <summary>
    /// Vowel anchor after offset in milliseconds.
    /// </summary>
    public double Preutterance { get; init; }

    /// <summary>
    /// Crossfade region after offset in milliseconds; may be negative.
    /// </summary>
    public double Overlap { get; init; }
}

/// <summary>
/// Metadata-only UTAU voicebank supporting stream and desktop resource providers.
/// </summary>
public sealed class Voicebank {
    private readonly FrozenDictionary<string, VoicebankSample> aliases;
    private readonly Dictionary<int, (string Prefix, string Suffix)> pitchMap;

    /// <summary>
    /// Resource provider retained for lazy sample acquisition.
    /// </summary>
    public IVoicebankResources Resources { get; }

    /// <summary>
    /// Loads oto.ini and optional prefix.map files; WAV and frequency data stay unopened.
    /// </summary>
    /// <param name="resources">Immutable authorized voicebank inventory.</param>
    /// <param name="encoding">Metadata encoding, defaulting to Shift-JIS for UTAU banks.</param>
    public Voicebank(IVoicebankResources resources, Encoding? encoding = null) {
        Resources = resources;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        encoding ??= Encoding.GetEncoding(932);
        var samples = new Dictionary<string, VoicebankSample>(StringComparer.Ordinal);
        pitchMap = [];
        foreach (var name in resources.Names.Where(name => Path.GetFileName(name).Equals("oto.ini", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)) {
            using var stream = resources.Open(name);
            using var reader = new StreamReader(stream, encoding, true);
            var parent = name.Contains('/') ? name[..(name.LastIndexOf('/') + 1)] : "";
            while (reader.ReadLine() is { } line) {
                var equal = line.IndexOf('=');
                if (equal < 1 || line.StartsWith('#')) continue;
                var fields = line[(equal + 1)..].Split(',');
                if (fields.Length < 6) throw new InvalidDataException($"Invalid oto record in {name}.");
                var file = line[..equal].Replace('\\', '/');
                var resource = parent + file;
                if (!resources.Names.Contains(resource)) throw new InvalidDataException($"Missing oto sample {resource}.");
                var alias = fields[0].Length == 0 ? Path.GetFileNameWithoutExtension(file) : fields[0];
                double Number(int index) {
                    if (!double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                        throw new InvalidDataException($"Invalid oto timing in {name}.");
                    return value;
                }
                samples.TryAdd(alias, new VoicebankSample { Resource = resource, Alias = alias, Offset = Number(1), Consonant = Number(2),
                    Cutoff = Number(3), Preutterance = Number(4), Overlap = Number(5) });
            }
        }
        if (samples.Count == 0) throw new InvalidDataException("Voicebank contains no oto aliases.");
        aliases = samples.ToFrozenDictionary(StringComparer.Ordinal);
        var mapName = resources.Names.FirstOrDefault(name => name.Equals("prefix.map", StringComparison.OrdinalIgnoreCase));
        if (mapName != null) {
            using var stream = resources.Open(mapName);
            using var reader = new StreamReader(stream, encoding, true);
            while (reader.ReadLine() is { } line) {
                var parts = line.Split('\t');
                if (parts.Length >= 3 && TryMidi(parts[0], out var midi)) pitchMap[midi] = (parts[1], parts[2]);
            }
        }
    }

    /// <summary>
    /// Resolves a pitch-mapped alias before its unmodified fallback.
    /// </summary>
    /// <param name="alias">English X-SAMPA alias.</param>
    /// <param name="midi">Requested MIDI pitch.</param>
    /// <returns>Sample metadata, or null when the voicebank cannot realize the alias.</returns>
    public VoicebankSample? Find(string alias, int midi) {
        if (pitchMap.Count != 0) {
            var map = pitchMap.MinBy(pair => Math.Abs(pair.Key - midi)).Value;
            if (aliases.TryGetValue(map.Prefix + alias + map.Suffix, out var pitched)) return pitched;
        }
        return aliases.GetValueOrDefault(alias);
    }

    private static bool TryMidi(string value, out int midi) {
        midi = 0;
        if (value.Length < 2) return false;
        var note = "C D EF G A B".IndexOf(char.ToUpperInvariant(value[0]));
        if (note < 0) return false;
        var position = 1;
        if (value[position] == '#') { note++; position++; }
        if (!int.TryParse(value[position..], out var octave)) return false;
        midi = (octave + 1) * 12 + note;
        return midi is >= 0 and <= 127;
    }
}
