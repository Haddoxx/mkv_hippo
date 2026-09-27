# MKV Hippo — Implementation Brief

Ground-up rewrite of the batch MKV track-stripper concept (no code reused from samehb/MKVStrip).
Windows GUI app that removes unwanted audio/subtitle tracks from all `.mkv` files under a
directory tree, losslessly, by driving `mkvmerge`. This document is the complete spec: implement
it end-to-end, build the final binary, and leave the repo in a committed, working state.

---

## 0. Hard requirements (do not deviate)

1. **Name:** MKV Hippo. Repo directory: `mkv_hippo`. Assembly/exe: `MKVHippo.exe`.
2. **No code from MKVStrip.** Clean-room implementation against this spec only.
3. **Parallelism control:** a radio-button group with exactly four options — `1`, `2`, `3`, `4` —
   selecting how many files are processed concurrently.
4. **Live rescaling:** the radio group stays enabled while a batch is running.
   - **Increase** (e.g. 2 → 4): additional worker slots are dispatched immediately.
   - **Decrease** (e.g. 4 → 2): nothing is killed. Running jobs continue; when the next job
     finishes (by definition the one closest to completion), its slot is *not* backfilled until
     the active count is at or below the new target. Repeat until drained to target.
5. **Deliverable:** a self-contained single-file `win-x64` binary produced by `dotnet publish`,
   plus a git repo with a clean history and passing tests.

## 1. Stack and project layout

- .NET 8, C#. UI: WinForms (`net8.0-windows`). Engine: plain `net8.0` class library so all
  logic is unit-testable on Linux (the UI cannot be run in CI, the engine can).
- If building on Linux/macOS, add `<EnableWindowsTargeting>true</EnableWindowsTargeting>` to the
  App project so the `net8.0-windows` WinForms target compiles. Install the .NET 8 SDK first if
  absent (`https://dot.net/v1/dotnet-install.sh --channel 8.0`).

Original spec layout (see §9 for what was added since):

```
mkv_hippo/
├── CLAUDE.md                  (this file)
├── README.md                  (usage, build instructions)
├── LICENSE                    (GPLv3)
├── .gitignore                 (standard dotnet: bin/, obj/, *.user, publish/, dist/)
├── Directory.Build.props      (PathMap + embedded PDBs; see §9 privacy posture)
├── MkvHippo.sln
├── .github/workflows/ci.yml   (engine tests on Linux, app build on Windows, dep audit)
├── src/
│   ├── MkvHippo.Core/         net8.0 class library — all logic lives here
│   │   ├── Mkv/
│   │   │   ├── MkvIdentifier.cs      runs `mkvmerge -J`, parses JSON
│   │   │   ├── TrackPlan.cs          decides keep/drop per file
│   │   │   └── MkvRemuxer.cs         runs the mux, interprets exit codes
│   │   ├── Processes/
│   │   │   ├── IProcessRunner.cs     child-process seam (tests stub this)
│   │   │   └── ProcessRunner.cs      the real implementation
│   │   ├── Scheduling/
│   │   │   └── AdaptiveScheduler.cs  dynamic-concurrency dispatcher (§3)
│   │   ├── Reporting/
│   │   │   ├── ProgressEvents.cs
│   │   │   ├── ThroughputMeter.cs    smoothed MB/s
│   │   │   └── BottleneckStats.cs    session bottleneck verdict
│   │   ├── BatchRunner.cs            scan → plan → schedule → report
│   │   ├── MkvmergeLocator.cs        finds mkvmerge (§2)
│   │   └── OutputPathMapper.cs       input→output mapping, root validation
│   └── MkvHippo.App/          net8.0-windows WinForms shell
│       ├── MainForm.cs / MainForm.Designer.cs
│       ├── ResourceMonitor.cs        PDH/NIC sampling for the gauges
│       ├── hippo.ico
│       └── Program.cs
└── tests/
    ├── fixtures/              committed `mkvmerge -J` outputs
    └── MkvHippo.Core.Tests/   xunit
```

## 2. mkvmerge interface

- **Locate `mkvmerge`** in this order: (1) next to `MkvHippo.exe`, (2) `PATH`,
  (3) `C:\Program Files\MKVToolNix\mkvmerge.exe`. Surface a clear error in the UI if not found.
  Do not bundle it (licensing/size); README tells the user to install MKVToolNix.
- **Identify:** `mkvmerge -J "<file>"` → parse with `System.Text.Json`. Use `tracks[].id`,
  `tracks[].type` (`video|audio|subtitles`), `tracks[].properties.language` and
  `properties.language_ietf` (match either against user filters). Never regex-scrape text output.
- **Remux:** `mkvmerge --quiet -o "<out>" [-a id,id | --no-audio] [-s id,id | --no-subtitles] "<in>"`.
  Success = exit code 0 or 1 (1 = completed with warnings; report warnings, keep the file).
  Exit code 2 = failure; report and delete any partial output file.
- Every `Process` use must `WaitForExit()` and read stdout/stderr asynchronously (avoid the
  classic redirected-stream deadlock).

## 3. AdaptiveScheduler — the live-rescaling core

Do not use `SemaphoreSlim` (permits can't be revoked cleanly). Implement an explicit dispatcher:

```csharp
public sealed class AdaptiveScheduler
{
    private readonly object _gate = new();
    private readonly Queue<Func<CancellationToken, Task>> _pending = new();
    private int _active;
    private int _target;              // 1..4, settable at any time

    public void SetTarget(int n)      // called from UI thread on radio change
    {
        lock (_gate) { _target = Math.Clamp(n, 1, 4); }
        TryDispatch();                // increases take effect immediately;
    }                                 // decreases drain naturally (see OnCompleted)

    private void TryDispatch()
    {
        while (true)
        {
            Func<CancellationToken, Task> job;
            lock (_gate)
            {
                if (_active >= _target || _pending.Count == 0) return;
                job = _pending.Dequeue();
                _active++;
            }
            _ = RunAsync(job);        // fire; completion re-enters via OnCompleted
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> job)
    {
        try { await job(_ct); }
        finally
        {
            lock (_gate) { _active--; }
            TryDispatch();            // backfills ONLY if _active < _target
        }
    }
}
```

Semantics this yields, exactly as required: raising the target dispatches new jobs at once;
lowering it never interrupts work — the next completion simply isn't replaced until
`_active <= _target`. Include `CancellationToken` support for a Stop button: cancel prevents new
dispatch and kills child `mkvmerge` processes (`Process.Kill(entireProcessTree: true)`), deleting
partial outputs.

**Unit tests (must pass, run on Linux):** using fake jobs with controllable completion —
(a) target 2 → 4 mid-run starts two more immediately; (b) target 4 → 1 lets all four running jobs
finish, backfills nothing until active drops below 1, then proceeds strictly one at a time;
(c) target 4 → 2 backfills only after two completions; (d) no job
ever runs with `active > target` at dispatch time; (e) queue drains fully; (f) cancellation stops
dispatch.

## 4. Planning logic

Per file: identify first, always (both modes — this fixes MKVStrip's blind-remux behaviour).

- **Language mode:** user supplies comma-separated language lists for audio and for subtitles
  (e.g. `eng, jpn`). Match case-insensitively against `language` or `language_ietf`; treat
  `und`/missing as its own matchable token `und`. Keyword `none` = drop all tracks of that type;
  empty box = keep all tracks of that type.
- **Track-ID mode:** comma-separated mkvmerge track IDs per type; same `none`/empty semantics.
- **Skip rule:** if the plan keeps every existing audio and subtitle track, do not remux; report
  "already clean".
- **Safety rule:** if an audio filter matches zero of the file's audio tracks (and the file has
  audio, and the filter isn't `none`), skip with a warning rather than emit a silent file.
  **Not** the same for subtitles (revised — see §9 v0.1.3): a zero-match subtitle filter is an
  advisory, not a skip. Dropping every subtitle is what the filter asked for and the result is
  still a usable file, whereas blocking would also discard the audio filtering that did match.
- Output path mirrors the relative subpath under the chosen output root; create directories.
  Refuse roots that overlap in **either** direction — output inside input, or input inside
  output (revised; see §9 v0.1.3). Never modify source files.

## 5. UI spec (WinForms, single window)

- Input folder + Browse; Output folder + Browse (default: sibling of input named `<input>-hippo`).
- Mode toggle: `Languages` / `Track IDs`; two text boxes: Audio filter, Subtitle filter.
- **Parallel files** group box: radio buttons `1 2 3 4`, default `2`, enabled at all times;
  `CheckedChanged` → `scheduler.SetTarget(n)`. When a reduction is pending, show
  "scaling down: N active → target M" in the status strip until drained.
- Buttons: `Scan` (identify-only report, grouped by identical track layout — run identifies
  through the same scheduler, they're cheap header reads), `Start`, `Stop`.
- Log: read-only `TextBox`/`RichTextBox`, append via `AppendText` only (never `Text +=`), marshal
  with `IProgress<T>`. Per-file lines: `[ok] name — removed a:2 s:3`, `[skip] already clean`,
  `[warn] …`, `[fail] …`. Overall counter `processed/total` plus a progress bar.
- Footer summary on completion: files processed / skipped / failed, bytes in → bytes out, elapsed.

## 6. Build, test, package

```bash
dotnet test                                            # engine tests must be green
dotnet publish src/MkvHippo.App -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:EnableWindowsTargeting=true                       # omit flag if building on Windows
```

Copy the published `MkvHippo.exe` to `dist/` and verify it exists and is > 1 MB. The UI cannot be
smoke-tested on Linux; compensate with full engine coverage (scheduler, JSON parsing against
fixture `mkvmerge -J` outputs committed under `tests/fixtures/`, plan logic including skip and
safety rules, output-path mapping, exit-code interpretation).

## 7. Git

```bash
git init && git add .gitignore README.md CLAUDE.md && git commit -m "Initial commit: MKV Hippo spec"
# then commit in logical units:
#   core: identifier + plan logic (+ tests)
#   core: adaptive scheduler (+ tests)
#   core: remuxer + batch runner (+ tests)
#   app: WinForms shell
#   build: publish config, dist binary path in README
```

Do not create a remote unless `gh auth status` shows an authenticated account; if it does, ask
before running `gh repo create`.

## 8. Acceptance checklist

All met as of v0.1.3:

- [x] `dotnet test` green; scheduler tests cover cases (a)–(f) in §3.
- [x] `dist/MKVHippo.exe` produced, self-contained single file, win-x64.
- [x] Radio group functions during processing; scale-up immediate, scale-down drains without
      killing jobs.
- [x] Files needing no changes are skipped without remux in both modes.
- [x] Zero-match **audio** filters skip with warning (subtitles advise — §4); sources never
      modified; partial outputs cleaned on failure/cancel.
- [x] No text-scraping of mkvmerge output anywhere; JSON identify + exit codes only.
- [x] Repo committed in logical units with the layout of §1.

---

## 9. Status addendum (2026-07-03, updated 2026-07-05)

The brief above is fully implemented and shipped; treat it as the original spec, and this
section as the delta. Repo remote: `Haddoxx/mkv_hippo` — public, GPLv3 (`LICENSE`).
Releases: v0.1.0, v0.1.1, v0.1.2, v0.1.3 (current, released 2026-09-27). v0.1.2 and v0.1.3
carry the self-contained `MKVHippo.exe`; the v0.1.2 asset was rebuilt post-tag to include the
higher-fidelity icon, and the v0.1.0/v0.1.1 exes were deleted (pre-PathMap, embedded local
paths).

Privacy posture (2026-07-05): commit history carries only `Haddoxx
<Haddoxx@users.noreply.github.com>` (names and emails rewritten; git config matches), and
`Directory.Build.props` sets `PathMap` (checkout dir → `/src/`) plus `DebugType=embedded`
for all assemblies — published binaries contain no local usernames or paths (verify with
`strings` after a publish).

Features added beyond the original spec (all engine logic tested — for the current test
count run `dotnet test`; don't hardcode it here, it drifts):

- **MP4/M4V input** (2026-07-05): discovery accepts `.mkv`/`.mp4`/`.m4v`; the output is always
  `.mkv` (`OutputPathMapper.Map` swaps the extension — mkvmerge only writes Matroska).
  Non-Matroska sources are detected from `container.type` in the `-J` JSON
  (`MkvFileInfo.NeedsContainerConversion`), never from the extension, and always remux —
  an "already clean" MP4 becomes a pure container conversion (logged "converted to mkv").
  Same-stem collisions (`movie.mkv` + `movie.mp4` → both map to `movie.mkv`) are resolved
  race-free under AutoRename via in-flight output reservation in `BatchRunner`
  (`MakeUnique` takes an `isReserved` predicate).
- **Auto-scan on Start** when the current input folder hasn't been scanned yet.
- **Dual-form language display** in scan reports (`[en / eng]` — filters match either form).
- **Batch-level unmatched-filter warnings**: filter values that matched no track in any file
  are reported after the run (suppressed after a Stop).
- **Kept-track log summaries** per file (`kept a:eng s:all`) — `s:all` flags an accidentally
  empty filter; filter placeholders read "empty = keep all".
- **Destination overwrite policy**: Auto rename (default, Windows-style `name (1).mkv` via
  `OutputPathMapper.MakeUnique`) or Overwrite; resolved paths flow through
  `FileOutputResolvedEvent`.
- **Resource gauges + bottleneck verdict**: status-bar CPU (`% Processor Utility` via PDH
  English-name counters), disk active time, busiest-NIC network %, and MB/s throughput at
  500 ms; per-session likely-bottleneck summary in the log (`ThroughputMeter`,
  `BottleneckStats` in Core; `ResourceMonitor` in App).
- **App icon** embedded (`src/MkvHippo.App/hippo.ico`; 10 frames 16–256 incl. 96/128,
  rebuilt 2026-07-05 with gamma-correct Lanczos + per-size unsharp from the 1024px master).
  Source artwork, master, SVG trace and rebuild recipe live only in git history — locate
  them with `git log --all -- icon/` (don't pin hashes; they change on history rewrites).
- Versioned title bar "MKV Hippo v<Version> by Haddoxx" — driven by `<Version>` in
  `MkvHippo.App.csproj`.

### v0.1.3 (2026-09-01) — destination-lifecycle and shutdown fixes

From a code review of v0.1.2; all five findings plus one adjacent race. 124 tests green.

- **Muxes write to a working file, never straight to the destination.** mkvmerge truncates its
  `-o` target on open, so under *Overwrite* a failure or a Stop destroyed the previous output
  before anything could be salvaged. `MkvRemuxer` now muxes to
  `<dest>.<token>.mkvhippo-tmp` in the destination directory and `File.Move(overwrite: true)`s
  it into place only on exit 0/1; failure and cancellation delete the working file and leave
  the destination untouched. `FileOutputResolvedEvent` gained `WorkingPath` so the throughput
  gauge measures the file that is actually growing.
- **Same-destination sources are settled before dispatch under *Overwrite*.** Auto rename
  already reserved unique names; Overwrite had no such path, so `movie.mkv` + `movie.mp4`
  raced. `BatchRunner` now pre-computes destinations: first in scan order wins, the rest get
  `FileOutcome.SkippedCollision` naming the winner.
- **`ProcessRunner` only treats a process it actually killed as cancelled.** Previously a mux
  that finished microseconds before Stop threw `OperationCanceledException` and its finished
  output was deleted.
- **`BatchRunner` owns cancellation.** `RunAsync`/`ScanAsync` link their `ct` with
  `AdaptiveScheduler.Token` (new), register `AdaptiveScheduler.CancelPending()` (new) to drop
  the queue, stop the enqueue loop, and pass the linked token to jobs — so cancelling either
  token works, in both directions, and `WasCancelled` reflects both.
- **Closing the window mid-batch cancels.** `MainForm.OnFormClosing` cancels `_cts` and defers
  the close (15 s bound; a second click forces it) instead of orphaning `mkvmerge` children.
- **`MkvTrack.LayoutKey` includes the codec**, which the scan report prints — differing codecs
  no longer collapse into one layout group shown under the first file's codec.

Verified against real mkvmerge on Linux via a throwaway harness over `MkvHippo.Core`
(auto-rename, overwrite collision, write failure, mid-mux Stop): destinations preserved, no
`.mkvhippo-tmp` leftovers, no orphaned children. The WinForms close path is compile-only here.

### v0.1.3, second pass (2026-09-26) — full-project review fixes

A review of the whole tree (code + docs, not just a diff) found eleven items; all are fixed
here and shipped in v0.1.3.

- **Overlapping roots can no longer destroy a source file.** `EnsureValidRoots` guarded only
  "output inside input". The reverse was allowed and was the dangerous one: with input
  `<root>/a/b` and output `<root>/a`, a source at `a/b/b/x.mkv` maps to `a/b/x.mkv` — on top of
  another source. Reproduced against real mkvmerge: under Overwrite the source went from 3
  tracks to 2 and the run still reported `ok=1 skipped=1 failed=0`. Both directions are now
  refused, and `IsInsideOrEqual` resolves a symlink/junction on the final path component so two
  names for one directory compare equal (a link *mid*-path is still not followed — .NET exposes
  no full canonicalization).
- **A zero-match subtitle filter no longer discards the whole file** (spec §4 revised). It was
  a `SkipNoMatch` like the audio case, but the "don't emit a silent file" rationale does not
  transfer: the common `eng`/`eng` filter pair produced *nothing at all* for every file lacking
  English subtitles. It is now `TrackPlan.Advisory`, the remux proceeds with subtitles dropped,
  and the result is `OkWithWarnings` so the log still says so.
- **Leftover working files are swept.** A crash or power loss left `.mkvhippo-tmp` files
  forever. `MkvRemuxer.SweepWorkingFiles` clears them from the output tree at batch start and
  reports a `WorkingFilesSweptEvent`; a file another instance is actively muxing is locked on
  Windows, so its delete fails and it is left alone.
- **Auto-scan no longer identifies every file twice.** Measured 3 files → 6 `mkvmerge -J`
  calls. `ScanReport.Identified` now carries the per-file results and `RunAsync` takes an
  optional `preIdentified`. `MainForm` passes it **only** for a scan that just ran — an older
  scan (`HasScanned`) may predate edits, so that path still identifies fresh.
- **PDH P/Invokes pinned to System32** (`DefaultDllImportSearchPaths`). The exe's own directory
  precedes System32 in the load order, and users are told to drop `mkvmerge.exe` beside
  `MKVHippo.exe`, so that directory is not necessarily trustworthy.
- **Test dependencies bumped**, clearing two High-severity transitive advisories
  (`System.Net.Http` / `System.Text.RegularExpressions` 4.3.0, via Test.Sdk 17.8.0). They were
  build-time only — confirmed absent from the shipped exe. `xunit` stays on the 2.x line
  (2.9.3, latest supported); the whole v2 line is flagged Legacy in favour of xunit.v3, which
  is a migration, not a bump — deliberately deferred.
- **CI added** (`.github/workflows/ci.yml`): engine tests on Linux, `-warnaserror` app build on
  Windows, and a dependency audit that fails on any vulnerable package. All three were
  validated locally before committing.
- **Docs**: this file contradicted itself (said v0.1.2 was current next to a v0.1.3 section;
  "111 tests green" next to "124"), the §8 checklist was still unticked, and the §1 tree used
  the wrong directory name and omitted eight files. README claimed "sources are never
  modified", which the root bug falsified. The release routine now says to name the release
  asset explicitly rather than glob `dist/`, which holds a local `mkvmerge.exe`.

### Startup performance: investigated 2026-09-27, not pursued

Prompted by ".NET apps are slow to start, due to the bulk". The bulk turned out not to be the
cause. Measured on the Windows host through WSL interop, one harness for every row — launch,
poll until a visible >200px top-level window owned by the process exists, 12–14 runs, median:

| shell | binary | median |
|---|---|---|
| raw Win32 (FPC, hand-built) | 134 KB | **32.7 ms** |
| Lazarus LCL, win32 widgetset | 2.55 MB | **48.6 ms** |
| MKVHippo as shipped (.NET WinForms) | 154.5 MB | **268.8 ms** |

Where the 270 ms actually goes — none of it is size:

- Cold start (single-file extraction cache wiped) 312 ms vs 271 ms warm, so unpacking 155 MB
  costs ~40 ms and only on first run. Every later launch pays nothing for the bulk.
- `PublishReadyToRun` measured 269.6 ms — no change, so JIT is not the cost either.
- The remainder is CLR + WinForms initialisation. It cannot be trimmed or AOT'd away:
  `PublishAot`/trimming hard-fail with `NETSDK1175: Windows Forms is not supported or
  recommended with trimming enabled`.

Packaging sizes measured at the same time: self-contained single-file 154.5 MB (current);
`+EnableCompressionInSingleFile` 68.6 MB but it decompresses on **every** launch — halves the
download and makes startup worse, so do not use it if startup is the concern;
self-contained + R2R 170.1 MB; framework-dependent + R2R 0.6 MB (needs the .NET 8 Desktop
Runtime installed — not present on the dev machine, so its startup is unmeasured).

Conclusion: only replacing WinForms helps. Lazarus/LCL captures ~82% of the available win
(220 of 236 ms) and is the closest structural match — visual form designer, native Win32
controls, OS-provided DPI and theming — so `MainForm.Designer.cs` would port near-mechanically.
Raw Win32 (or Rust + windows-rs) buys a further 16 ms for substantially more work. Deferred as
not worth 2–4 weeks of UI porting against a working, reviewed app; the Core test suite would
serve as the conformance suite for any future port. The option does not expire.

Two traps for anyone repeating this:

- `Process.MainWindowHandle` returns LCL's hidden 0×0 utility window, not the form, so a naive
  harness times the wrong thing. Enumerate visible, process-owned top-level windows instead and
  require a plausible size. Symptom that caught it: child-control count 0 for LCL vs 29 for the
  Win32 build.
- The LCL prototype built its form in code via `CreateNew` (LCL will not show a resource-less
  form through `Application.CreateForm`). A real port streams a `.lfm` from the designer, so
  48.6 ms is a mild under-estimate.

Release routine: bump `<Version>` in `src/MkvHippo.App/MkvHippo.App.csproj` → `dotnet test` →
publish per §6 → copy to `dist/` → commit/push the source → `gh release create v<X.Y.Z>
dist/MKVHippo.exe`. Name that asset explicitly, never `dist/*`: `dist/` is gitignored scratch
and may hold a local `mkvmerge.exe`, which §2 forbids shipping on licensing grounds.
