using Qt7AudioConverter;

bool mono = args.Contains("--mono");
string[] paths = args.Where(a => a != "--mono").ToArray();

if (paths.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: qt7convert [--mono] <input.wav|input.mp3|folder> [output.wav]");
    Console.Error.WriteLine("  With a folder, every *.wav and *.mp3 inside is converted to <name>.qt7.wav.");
    Console.Error.WriteLine("  --mono  downmix MP3 conversions to a single channel.");
    return 1;
}

string input = paths[0];

if (Directory.Exists(input))
{
    if (paths.Length == 2)
    {
        Console.Error.WriteLine("An output path cannot be combined with a folder input.");
        return 1;
    }

    int converted = 0, failed = 0;
    foreach (string file in Directory.EnumerateFiles(input))
    {
        if (!IsConvertible(file))
            continue;
        string target = Path.ChangeExtension(file, null) + ".qt7.wav";
        try
        {
            string note = ConvertFile(file, target, mono);
            Console.WriteLine($"Converted: {file} -> {target}{note}");
            converted++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed:    {file} ({ex.Message})");
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

string output = paths.Length == 2
    ? paths[1]
    : Path.ChangeExtension(input, null) + ".qt7.wav";

try
{
    string note = ConvertFile(input, output, mono);
    Console.WriteLine($"Converted: {input} -> {output}{note}");
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

static string ConvertFile(string inputFile, string outputFile, bool mono)
{
    if (Path.GetExtension(inputFile).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
    {
        var info = Mp3ToQt7Converter.Convert(inputFile, outputFile, mono);
        var changes = new List<string>();
        if (info.SourceSampleRate != info.OutputSampleRate)
            changes.Add($"{info.SourceSampleRate} Hz -> {info.OutputSampleRate} Hz");
        if (info.SourceChannels != info.OutputChannels)
            changes.Add($"{ChannelName(info.SourceChannels)} -> {ChannelName(info.OutputChannels)}");
        return changes.Count > 0 ? $" ({string.Join(", ", changes)})" : "";
    }

    int sampleRate = WavQt7Converter.Convert(inputFile, outputFile);
    return sampleRate is 44100 or 0
        ? ""
        : $" (note: {sampleRate} Hz — old devices may only accept 44100 Hz)";
}

static string ChannelName(int channels) => channels switch
{
    1 => "mono",
    2 => "stereo",
    _ => $"{channels} channels",
};
