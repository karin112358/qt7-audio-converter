# qt7-audio-converter

Converts audio files into the classic WAV layout that QuickTime 7-era
devices and software can read — losslessly for WAV input, and by decoding
for MP3 input. Runs on Windows and macOS with nothing to install.

## The problem

WAV files saved by QuickTime 10 (and other modern Apple software) contain
`JUNK` and `FLLR` padding chunks so that the audio data starts at a
4096-byte aligned offset. These chunks are legal per the RIFF specification,
but many old hardware samplers, players, and programs use naive WAV parsers
that expect `fmt ` directly after the header and `data` directly after that —
they refuse or misread such files.

**qt7convert** rewrites the file keeping only the `fmt `, `fact` (if present)
and `data` chunks in canonical order. The audio payload is copied
byte-for-byte — the conversion is completely lossless, no re-encoding takes
place.

MP3 files are also supported: they are decoded to 16-bit PCM (using the
managed, cross-platform [NLayer](https://www.nuget.org/packages/NLayer)
decoder — no native dependencies) and written as canonical WAV files.

## Download

Grab the build for your system from the [latest release](../../releases/latest):

| System | Asset |
|---|---|
| Windows (64-bit) | `qt7convert-win-x64.zip` |
| macOS, Intel (macOS 10.15+, incl. Big Sur) | `qt7convert-osx-x64.tar.gz` |
| macOS, Apple Silicon (M1 and later) | `qt7convert-osx-arm64.tar.gz` |

The executables are self-contained — no .NET installation or other runtime
is required.

**Windows:** unzip and run `qt7convert.exe` from a terminal.

**macOS:** unpack with `tar xzf qt7convert-osx-x64.tar.gz` (Archive Utility
works too). If the file was downloaded with a browser, clear the quarantine
flag once:

```bash
xattr -d com.apple.quarantine qt7convert
```

If running it gives "permission denied" (e.g. because the file was copied
without its permissions), make it executable once with `chmod +x qt7convert`.

## Usage

```bash
# Convert one file (writes recording.qt7.wav next to it)
./qt7convert recording.wav
./qt7convert song.mp3

# Choose the output name
./qt7convert recording.wav fixed.wav

# Convert every *.wav and *.mp3 in a folder
./qt7convert ~/Desktop/Recordings
```

Folder mode writes a `<name>.qt7.wav` copy for each file; originals are
never modified. Files that are already in canonical form pass through
unchanged, so it is safe to run the converter on everything.

Tip: in the macOS Terminal you can type `./qt7convert ` and then drag a
folder from Finder into the window to insert its path.

## Embedding in your own .NET application

The conversion logic lives in the `Qt7AudioConverter` class library,
which targets `netstandard2.0` — it works from .NET Framework, .NET 6/8+,
on Windows and macOS, with no native dependencies:

```csharp
using Qt7AudioConverter;

WavQt7Converter.Convert("input.wav", "output.wav");   // lossless chunk rewrite
Mp3ToQt7Converter.Convert("input.mp3", "output.wav"); // decode MP3 to PCM WAV
// stream overloads exist for both
```

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build                                        # build everything
dotnet run --project src/Qt7AudioConverter.Cli -- input.wav

# Self-contained single-file executable, e.g. for Intel Macs:
dotnet publish src/Qt7AudioConverter.Cli -c Release -r osx-x64 \
  --self-contained -p:PublishSingleFile=true -p:DebugType=none -o publish/osx-x64
```

Runtime identifiers: `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`.

## License

[MIT](LICENSE). MP3 decoding by [NLayer](https://github.com/naudio/NLayer)
(MIT license).
