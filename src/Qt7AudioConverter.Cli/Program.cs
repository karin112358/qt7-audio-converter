using Qt7AudioConverter;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: qt7convert <input.wav|input.mp3|folder> [output.wav]");
    Console.Error.WriteLine("  With a folder, every *.wav and *.mp3 inside is converted to <name>.qt7.wav.");
    return 1;
}

string input = args[0];

if (Directory.Exists(input))
{
    if (args.Length == 2)
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
            ConvertFile(file, target);
            Console.WriteLine($"Converted: {file} -> {target}");
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

string output = args.Length == 2
    ? args[1]
    : Path.ChangeExtension(input, null) + ".qt7.wav";

try
{
    ConvertFile(input, output);
    Console.WriteLine($"Converted: {input} -> {output}");
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

static void ConvertFile(string inputFile, string outputFile)
{
    if (Path.GetExtension(inputFile).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        Mp3ToQt7Converter.Convert(inputFile, outputFile);
    else
        WavQt7Converter.Convert(inputFile, outputFile);
}
