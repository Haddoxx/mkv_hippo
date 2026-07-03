# MKV Hippo

A Windows GUI tool that batch-removes unwanted audio and subtitle tracks from `.mkv` files,
losslessly, by driving [mkvmerge](https://mkvtoolnix.download/) from MKVToolNix. Point it at a
folder tree, tell it which languages (or track IDs) to keep, and it remuxes every MKV underneath
into a mirrored output tree — never touching your source files.

## Features

- **Recursive batch processing** — scans an entire directory tree for `.mkv` files.
- **Language or Track-ID filtering** — keep audio/subtitle tracks by language code
  (`eng, jpn`), by mkvmerge track ID, drop all of a type (`none`), or keep all (leave blank).
- **Identify-first** — every file's real track layout is read via `mkvmerge -J` before any
  decision; files that already match your filters are skipped ("already clean"), and filters
  matching zero tracks skip with a warning instead of producing a silent file. Starting a batch
  on a folder you haven't scanned yet runs the scan automatically first.
- **Filter feedback** — scan lines show both language forms when they differ (e.g.
  `[en / eng]`; a filter matches either), and the end-of-run summary warns about filter values
  that matched no tracks in any file (likely typos).
- **Live parallelism control** — process 1–4 files concurrently, adjustable *while running*:
  scale-up dispatches new jobs immediately; scale-down never kills a running job, it just stops
  backfilling slots until the active count drains to the new target.
- **Lossless** — pure remux, no re-encoding. Sources are never modified.

## Requirements

- Windows x64.
- [MKVToolNix](https://mkvtoolnix.download/) installed (MKV Hippo looks for `mkvmerge.exe` next
  to itself, then on `PATH`, then at `C:\Program Files\MKVToolNix\mkvmerge.exe`).

## Usage

1. Launch `MKVHippo.exe`.
2. Pick an **Input folder**; the **Output folder** defaults to a sibling named `<input>-hippo`
   (the output folder must not be inside the input folder).
3. Choose **Languages** or **Track IDs** mode and fill the Audio / Subtitle filter boxes:
   - `eng, jpn` — keep only those languages (or IDs in Track-ID mode); `und` matches
     tracks with an undetermined/missing language.
   - `none` — drop **all** tracks of that type.
   - empty — keep all tracks of that type.
4. **Scan** for an identify-only report grouped by track layout, or **Start** to process
   (Start scans first if the input folder hasn't been scanned yet).
5. Adjust **Parallel files** (1–4) any time, even mid-batch. **Stop** cancels: pending files are
   not started, running `mkvmerge` processes are killed and their partial outputs deleted.

## Screenshots

*(coming soon)*

## Building from source

Requires the .NET 8 SDK.

```bash
dotnet test                                            # engine tests (run anywhere, incl. Linux)
dotnet publish src/MkvHippo.App -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableWindowsTargeting=true                       # omit flag if building on Windows
```

The self-contained single-file binary lands in
`src/MkvHippo.App/bin/Release/net8.0-windows/win-x64/publish/MKVHippo.exe`; copy it to `dist/`.

## Layout

- `src/MkvHippo.Core` — all engine logic (`net8.0`, fully unit-tested): mkvmerge JSON
  identification, keep/drop planning, remuxing, adaptive scheduler, batch runner.
- `src/MkvHippo.App` — WinForms shell (`net8.0-windows`).
- `tests/MkvHippo.Core.Tests` — xunit tests, including `mkvmerge -J` fixtures under
  `tests/fixtures/`.
