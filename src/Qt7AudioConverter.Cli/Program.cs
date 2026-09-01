using System.Globalization;
using Qt7AudioConverter;

bool mono = false;
bool shortNames = false;
bool normalize = false;
float normalizeDbfs = 0f;
float volume = 1f;
var paths = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mono":
            mono = true;
            break;
        case "--short":
            shortNames = true;
            break;
        case "--volume":
            if (i + 1 >= args.Length
                || !float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out volume)
                || volume <= 0f)
            {
                Console.Error.WriteLine("--volume needs a positive number in English format, e.g. --volume 1.5");
                return 1;
            }
            i++;
            break;
        case "--normalize":
            normalize = true;
            if (i + 1 < args.Length
                && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float db))
            {
                if (db > 0f)
                {
                    Console.Error.WriteLine("--normalize target must be 0 dBFS (maximum) or negative, e.g. --normalize -1");
                    return 1;
                }
                normalizeDbfs = db;
                i++;
            }
            break;
        default:
            paths.Add(args[i]);
            break;
    }
}

if (normalize && volume != 1f)
{
    Console.Error.WriteLine("--volume and --normalize cannot be combined.");
    return 1;
}

if (paths.Count is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: qt7convert [--mono] [--volume <factor>] [--normalize [dBFS]] [--short] <input.wav|input.mp3|folder> [output.wav|output-folder]");
    Console.Error.WriteLine("  With a folder, every *.wav and *.mp3 inside is converted to <name>-qt7.wav");
    Console.Error.WriteLine("  in the output folder (default: a 'qt7' subfolder of the input folder).");
    Console.Error.WriteLine("  --mono              downmix conversions to a single channel.");
    Console.Error.WriteLine("  --volume <factor>   multiply loudness, e.g. 1.5 = 50% louder, 0.5 = half.");
    Console.Error.WriteLine("  --normalize [dBFS]  raise each file to the given peak level (default 0 = maximum).");
    Console.Error.WriteLine("  --short             write 8-character output names for old devices (e.g. 08FINGER.wav).");
    return 1;
}

string input = paths[0];

if (Directory.Exists(input))
{
    string outDir = paths.Count == 2 ? paths[1] : Path.Combine(input, "qt7");
    Directory.CreateDirectory(outDir);

    Console.WriteLine($"Converting: {Path.GetFullPath(input)}");
    Console.WriteLine($"Output:     {Path.GetFullPath(outDir)}");
    int converted = 0, failed = 0;
    string suffix = NameSuffix(volume, normalize, normalizeDbfs);
    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (string file in Directory.EnumerateFiles(input))
    {
        if (created.Contains(file) || !IsConvertible(file))
            continue;
        string target = TargetPath(file, outDir, suffix, shortNames, usedNames);
        created.Add(target);
        try
        {
            float fileVolume = volume;
            string? note = null;
            if (normalize)
                fileVolume = NormalizeFactor(file, mono, normalizeDbfs, out note);
            ConvertFile(file, target, mono, fileVolume, namesOnly: true, note);
            converted++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Path.GetFileName(file)}");
            Console.Error.WriteLine($"    failed: {ex.Message}");
            failed++;
        }
    }
    Console.WriteLine($"{converted} file(s) converted, {failed} failed.");
    return failed == 0 ? 0 : 1;
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Input not found: {input}");
    return 1;
}

string output = paths.Count == 2
    ? paths[1]
    : TargetPath(input, Path.GetDirectoryName(input) ?? "", NameSuffix(volume, normalize, normalizeDbfs),
        shortNames, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

try
{
    float fileVolume = volume;
    string? note = null;
    if (normalize)
        fileVolume = NormalizeFactor(input, mono, normalizeDbfs, out note);
    ConvertFile(input, output, mono, fileVolume, namesOnly: false, note);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed: {ex.Message}");
    return 1;
}

static bool IsConvertible(string file)
{
    string name = Path.GetFileNameWithoutExtension(file);
    if (name.EndsWith(".qt7", StringComparison.OrdinalIgnoreCase)         // legacy output naming
        || name.Contains("-qt7", StringComparison.OrdinalIgnoreCase))
        return false; // skip our own output files
    string ext = Path.GetExtension(file);
    return ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
}

static string NameSuffix(float volume, bool normalize, float normalizeDbfs)
{
    if (normalize)
        return "-norm" + normalizeDbfs.ToString(CultureInfo.InvariantCulture).Replace('.', '_');
    if (volume != 1f)
        return "-vol" + volume.ToString(CultureInfo.InvariantCulture).Replace('.', '_');
    return "";
}

static float NormalizeFactor(string inputFile, bool mono, float targetDbfs, out string? note)
{
    float peak = Path.GetExtension(inputFile).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        ? Mp3ToQt7Converter.MeasurePeak(inputFile, mono)
        : WavQt7Converter.MeasurePeak(inputFile, mono);
    if (peak <= 0f)
    {
        note = "  (silent file, not normalized)";
        return 1f;
    }
    float target = (float)Math.Pow(10, targetDbfs / 20.0);
    float factor = target / peak;
    double peakDb = 20.0 * Math.Log10(peak);
    note = "  (normalized: peak " + peakDb.ToString("0.0", CultureInfo.InvariantCulture)
        + " -> " + targetDbfs.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS)";
    return factor;
}

static string TargetPath(string inputFile, string outDir, string extraSuffix, bool shortNames, HashSet<string> usedNames)
{
    string dir = outDir;
    if (!shortNames)
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(inputFile) + "-qt7" + extraSuffix + ".wav");

    var kept = new System.Text.StringBuilder();
    foreach (char c in Path.GetFileNameWithoutExtension(inputFile).ToUpperInvariant())
        if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            kept.Append(c);
    string name = kept.Length == 0 ? "SOUND" : kept.ToString();
    if (name.Length > 8) name = name[..8];
    if (!usedNames.Add(name))
    {
        for (int i = 2; ; i++)
        {
            string n = i.ToString();
            string candidate = (name.Length + n.Length <= 8 ? name : name[..(8 - n.Length)]) + n;
            if (usedNames.Add(candidate)) { name = candidate; break; }
        }
    }
    return Path.Combine(dir, name + ".wav");
}

static void ConvertFile(string inputFile, string outputFile, bool mono, float volume, bool namesOnly, string? noteOverride = null)
{
    string sourceDesc, outputDesc, note;
    long clipped;
    if (Path.GetExtension(inputFile).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
    {
        var info = Mp3ToQt7Converter.Convert(inputFile, outputFile, mono, volume);
        sourceDesc = Describe(info.SourceSampleRate, "MP3", info.SourceChannels, info.SourceDurationSeconds, inputFile);
        outputDesc = Describe(info.OutputSampleRate, "16-bit", info.OutputChannels, info.OutputDurationSeconds, outputFile);
        note = noteOverride ?? VolumeNote(volume);
        clipped = info.ClippedSamples;
    }
    else
    {
        var info = WavQt7Converter.Convert(inputFile, outputFile, mono, volume);
        sourceDesc = Describe(info.SourceSampleRate, $"{info.SourceBitsPerSample}-bit", info.SourceChannels, info.SourceDurationSeconds, inputFile);
        outputDesc = Describe(info.OutputSampleRate, $"{info.OutputBitsPerSample}-bit", info.OutputChannels, info.OutputDurationSeconds, outputFile);
        note = info.Lossless ? "  (lossless copy)" : noteOverride ?? VolumeNote(volume);
        clipped = info.ClippedSamples;
    }

    Console.WriteLine(namesOnly
        ? $"{Path.GetFileName(inputFile)} -> {Path.GetFileName(outputFile)}"
        : $"{inputFile} -> {outputFile}");
    Console.WriteLine($"    source: {sourceDesc}");
    Console.WriteLine($"    result: {outputDesc}{note}");
    if (clipped > 0)
        Console.WriteLine($"    warning: {clipped} sample(s) clipped - use a lower --volume or --normalize target");
}

static string Describe(int sampleRate, string bits, int channels, double durationSeconds, string file)
{
    long bytes = new FileInfo(file).Length;
    string size = bytes >= 1024 * 1024
        ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
    string duration = durationSeconds.ToString("0.0#", CultureInfo.InvariantCulture) + " s";
    return $"{sampleRate,6} Hz  {bits,7}  {ChannelName(channels),-6}  {duration,7}  {size,7}";
}

static string VolumeNote(float volume) => volume != 1f
    ? "  (volume x" + volume.ToString(CultureInfo.InvariantCulture) + ")"
    : "";

static string ChannelName(int channels) => channels switch
{
    1 => "mono",
    2 => "stereo",
    _ => $"{channels} channels",
};
