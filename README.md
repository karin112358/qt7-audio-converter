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
and `data` chunks in canonical order. For WAV files that are already
44.1 kHz with 8- or 16-bit integer PCM, the audio payload is copied
byte-for-byte — completely lossless, no re-encoding.

Legacy hardware (e.g. the Roland SPD-S) typically accepts *only* 44.1 kHz
8/16-bit WAV files. Modern DAW exports are often 48/96 kHz, 24-bit, or
32-bit float — those are automatically decoded, resampled, and written as
44.1 kHz 16-bit PCM. The tool prints what it changed, e.g.
`(48000 Hz -> 44100 Hz, 24-bit -> 16-bit)`.

MP3 files are also supported: they are decoded to 16-bit PCM (using the
managed, cross-platform [NLayer](https://www.nuget.org/packages/NLayer)
decoder — no native dependencies) and written as canonical WAV files at
**44.1 kHz** — MP3s encoded at other rates (32 kHz, 22.05 kHz, 48 kHz, …)
are resampled, since legacy devices commonly accept only 44.1 kHz. With
`--mono` the output is downmixed to a single channel.

## Download

Grab the build for your system from the [latest release](../../releases/latest):

| System | Asset |
|---|---|
| Windows (64-bit) | `qt7convert-win-x64.zip` |
| macOS, Intel (macOS 10.15+, incl. Big Sur) | `qt7convert-osx-x64.tar.gz` (or `.zip`) |
| macOS, Apple Silicon (M1 and later) | `qt7convert-osx-arm64.tar.gz` (or `.zip`) |

The executables are self-contained — no .NET installation or other runtime
is required.

**Windows:** unzip and run `qt7convert.exe` from a terminal.

**macOS:** unpack with `tar xzf qt7convert-osx-x64.tar.gz` (Archive Utility
works too); both the tar.gz and the zip are extracted by double-click in
Finder. If the file was downloaded with a browser, clear the quarantine
flag once:

```bash
xattr -d com.apple.quarantine qt7convert
```

If running it gives "permission denied" (e.g. because the file was copied
without its permissions), make it executable once with `chmod +x qt7convert`.

## Usage

```bash
# Convert one file (writes recording-qt7.wav next to it)
./qt7convert recording.wav
./qt7convert song.mp3

# Choose the output name
./qt7convert recording.wav fixed.wav

# Convert every *.wav and *.mp3 in a folder
./qt7convert ~/Desktop/Recordings

# Downmix conversions to mono (halves memory use on samplers)
./qt7convert --mono song.mp3

# Adjust loudness: 1.5 = 50% louder, 0.5 = half volume (English number format)
# The factor becomes part of the output name: song-qt7-vol1_5.wav
./qt7convert --volume 1.5 song.mp3

# Normalize: raise every file to maximum level (0 dBFS peak), or to a
# given peak level in dBFS. Output names get e.g. -qt7-norm0 / -qt7-norm-1.
./qt7convert --normalize ~/Desktop/Recordings
./qt7convert --normalize -1 song.mp3

# 8-character output names for old devices like the Roland SPD-S
# ("08 - Finger Snap.wav" -> 08FINGER.wav; collisions get numbered)
./qt7convert --short ~/Desktop/Recordings
```

Folder mode writes a `<name>-qt7.wav` copy for each file (with `--short`,
an 8-character name like `08FINGER.wav` instead); originals are
never modified. Files that are already in canonical form pass through
unchanged, so it is safe to run the converter on everything. Note that
`--mono`, `--volume`, or `--normalize` forces re-encoding of WAV files
that would otherwise be copied losslessly, and a too-high volume clips
(the tool warns and reports how many samples were affected). When
normalizing resampled files to 0 dBFS, a couple of clipped samples from
resampling overshoot are normal and inaudible; use e.g. `--normalize -0.5`
if you want none at all.

Tip: in the macOS Terminal you can type `./qt7convert ` and then drag a
folder from Finder into the window to insert its path.

## Embedding in your own .NET application

The conversion logic lives in the `Qt7AudioConverter` class library,
which targets `netstandard2.0` — it works from .NET Framework, .NET 6/8+,
on Windows and macOS, with no native dependencies:

```csharp
using Qt7AudioConverter;

WavQt7Converter.Convert("input.wav", "output.wav");   // lossless when already 44.1 kHz
                                                      // 8/16-bit, else 44.1 kHz 16-bit
Mp3ToQt7Converter.Convert("input.mp3", "output.wav"); // decode to 44.1 kHz PCM WAV
Mp3ToQt7Converter.Convert("input.mp3", "output.wav", downmixToMono: true);
WavQt7Converter.Convert("input.wav", "output.wav", volume: 1.5f); // 50% louder
// stream overloads exist for both; both return an info struct describing what changed
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
