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

```
mkv-hippo/
├── CLAUDE.md                  (this file)
├── README.md                  (usage, build instructions)
├── .gitignore                 (standard dotnet: bin/, obj/, *.user, publish/)
├── MkvHippo.sln
├── src/
│   ├── MkvHippo.Core/         net8.0 class library — all logic lives here
│   │   ├── Mkv/
│   │   │   ├── MkvIdentifier.cs      runs `mkvmerge -J`, parses JSON
│   │   │   ├── TrackPlan.cs          decides keep/drop per file
│   │   │   └── MkvRemuxer.cs         runs the mux, interprets exit codes
│   │   ├── Scheduling/
│   │   │   └── AdaptiveScheduler.cs  dynamic-concurrency dispatcher (§3)
│   │   ├── BatchRunner.cs            scan → plan → schedule → report
│   │   └── Reporting/ProgressEvents.cs
│   └── MkvHippo.App/          net8.0-windows WinForms shell
│       ├── MainForm.cs / MainForm.Designer.cs
│       └── Program.cs
└── tests/
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
  Same for subtitles.
- Output path mirrors the relative subpath under the chosen output root; create directories.
  Refuse an output root inside the input root. Never modify source files.

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

- [ ] `dotnet test` green; scheduler tests cover cases (a)–(f) in §3.
- [ ] `dist/MkvHippo.exe` produced, self-contained single file, win-x64.
- [ ] Radio group functions during processing; scale-up immediate, scale-down drains without
      killing jobs.
- [ ] Files needing no changes are skipped without remux in both modes.
- [ ] Zero-match filters skip with warning; sources never modified; partial outputs cleaned on
      failure/cancel.
- [ ] No text-scraping of mkvmerge output anywhere; JSON identify + exit codes only.
- [ ] Repo committed in logical units with the layout of §1.

---

## 9. Status addendum (2026-07-03)

The brief above is fully implemented and shipped; treat it as the original spec, and this
section as the delta. Repo remote: `Haddoxx/mkv_hippo` (private). Releases: v0.1.0, v0.1.1,
v0.1.2 (current), each with the self-contained `MKVHippo.exe` attached.

Privacy posture (2026-07-05): commit history carries only `Haddoxx
<Haddoxx@users.noreply.github.com>` (names and emails rewritten; git config matches), and
`Directory.Build.props` sets `PathMap` (checkout dir → `/src/`) plus `DebugType=embedded`
for all assemblies — published binaries contain no local usernames or paths (verify with
`strings` after a publish). The pre-PathMap v0.1.0/v0.1.1 exe assets were deleted from
their releases; v0.1.2 is the earliest downloadable exe.

Features added beyond the original spec (all engine logic tested; 111 tests green):

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
- **App icon** embedded (`src/MkvHippo.App/hippo.ico`); source artwork, 1024px master, SVG
  trace and rebuild recipe live only in git history (commits `be6f40f`, `e1d8d23`, `468c914`).
- Versioned title bar "MKV Hippo v<Version> by Haddoxx" — driven by `<Version>` in
  `MkvHippo.App.csproj`.

Release routine: bump `<Version>` in `src/MkvHippo.App/MkvHippo.App.csproj` → `dotnet test` →
publish per §6 → copy to `dist/` → commit/push → `gh release create v<X.Y.Z> dist/MKVHippo.exe`.
