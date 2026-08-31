# Changelog

## 1.1.0 (2026-08-31)

- MP3 conversions are now always written at 44.1 kHz: MP3s encoded at other
  sample rates (32 kHz, 22.05 kHz, 48 kHz, …) are resampled, since legacy
  devices commonly accept only 44.1 kHz.
- New `--mono` option downmixes MP3 conversions to a single channel.
- The CLI reports what changed (sample rate, channels) after each MP3
  conversion, and warns when a converted WAV uses a sample rate other than
  44.1 kHz (WAV conversion itself remains lossless and untouched).

## 1.0.0 (2026-08-31)

Initial release.

- Lossless conversion of WAV files saved by QuickTime 10 (with `JUNK`/`FLLR`
  padding chunks) into the canonical layout readable by QuickTime 7-era devices.
- MP3 to WAV conversion (decoded to 16-bit PCM via the managed NLayer decoder).
- `qt7convert` command-line tool: converts a single file or every `*.wav`/`*.mp3`
  in a folder, writing `<name>.qt7.wav` copies and leaving originals untouched.
- Embeddable `Qt7AudioConverter` class library targeting `netstandard2.0`.
- Self-contained executables for Windows x64, macOS Intel, and macOS Apple
  Silicon (minimum macOS 10.15).
