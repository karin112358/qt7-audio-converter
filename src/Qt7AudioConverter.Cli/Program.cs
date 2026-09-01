using System.Globalization;
using Qt7AudioConverter;

bool mono = false;
float volume = 1f;
var paths = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mono":
            mono = true;
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
        default:
            paths.Add(args[i]);
            break;
    }
}

if (paths.Count is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: qt7convert [--mono] [--volume <factor>] <input.wav|input.mp3|folder> [output.wav]");
    Console.Error.WriteLine("  With a folder, every *.wav and *.mp3 inside is converted to <name>.qt7.wav.");
    Console.Error.WriteLine("  --mono             downmix conversions to a single channel.");
    Console.Error.WriteLine("  --volume <factor>  multiply loudness, e.g. 1.5 = 50% louder, 0.5 = half.");
    return 1;
}

string input = paths[0];

if (Directory.Exists(input))
{
    if (paths.Count == 2)
    {
        Console.Error.WriteLine("An output path cannot be combined with a folder input.");
        return 1;
    }

    Console.WriteLine($"Converting: {Path.GetFullPath(input)}");
    int converted = 0, failed = 0;
    foreach (string file in Directory.EnumerateFiles(input))
    {
        if (!IsConvertible(file))
            continue;
        string target = Path.ChangeExtension(file, null) + ".qt7.wav";
        try
        {
            ConvertFile(file, target, mono, volume, namesOnly: true);
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
    : Path.ChangeExtension(input, null) + ".qt7.wav";

try
{
    ConvertFile(input, output, mono, volume, namesOnly: false);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed: {ex.Message}");
    return 1;
}

static bool IsConvertible(string file)
{
    if (file.EndsWith(".qt7.wav", StringComparison.OrdinalIgnoreCase))
        return false; // skip our own output files
    string ext = Path.GetExtension(file);
    return ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
}

static void ConvertFile(string inputFile, string outputFile, bool mono, float volume, bool namesOnly)
{
    string sourceDesc, outputDesc, note;
    long clipped;
    if (Path.GetExtension(inputFile).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
    {
        var info = Mp3ToQt7Converter.Convert(inputFile, outputFile, mono, volume);
        sourceDesc = Describe(info.SourceSampleRate, "MP3", info.SourceChannels, info.SourceDurationSeconds, inputFile);
        outputDesc = Describe(info.OutputSampleRate, "16-bit", info.OutputChannels, info.OutputDurationSeconds, outputFile);
        note = VolumeNote(volume);
        clipped = info.ClippedSamples;
    }
    else
    {
        var info = WavQt7Converter.Convert(inputFile, outputFile, mono, volume);
        sourceDesc = Describe(info.SourceSampleRate, $"{info.SourceBitsPerSample}-bit", info.SourceChannels, info.SourceDurationSeconds, inputFile);
        outputDesc = Describe(info.OutputSampleRate, $"{info.OutputBitsPerSample}-bit", info.OutputChannels, info.OutputDurationSeconds, outputFile);
        note = info.Lossless ? "  (lossless copy)" : VolumeNote(volume);
        clipped = info.ClippedSamples;
    }

    Console.WriteLine(namesOnly
        ? $"{Path.GetFileName(inputFile)} -> {Path.GetFileName(outputFile)}"
        : $"{inputFile} -> {outputFile}");
    Console.WriteLine($"    source: {sourceDesc}");
    Console.WriteLine($"    result: {outputDesc}{note}");
    if (clipped > 0)
        Console.WriteLine($"    warning: {clipped} sample(s) clipped - try a lower --volume");
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
