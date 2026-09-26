# MKV Hippo Code Review

Review date: 26 September 2026
Reviewed at: v0.1.3 (unreleased), commit `40b045b`
Scope: the whole tree — engine, WinForms shell, tests, build configuration and all
documentation. This supersedes the 1 September 2026 review, whose five findings were all
fixed in the first v0.1.3 pass.

**Status: all eleven findings below are fixed.** Verification for each is recorded inline.

Baseline after the fixes: **136 tests green**; the whole solution builds with `-warnaserror`
at 0 warnings; no vulnerable packages in any project.

---

## Findings

### 1. High — overlapping roots could destroy a source file *(fixed)*

`EnsureValidRoots` guarded only "output inside input". The reverse nesting was allowed and was
the dangerous direction. With input `<root>/a/b` and output `<root>/a`, `Map` sends
`a/b/b/x.mkv` → `a/b/x.mkv`, which lands **inside the input tree**, on top of another source.

Reproduced against real mkvmerge before the fix:

```
mode=overwrite   inputRoot=a/b   outputRoot=a
  source a/b/x.mkv before: 328c2691…  59344 bytes, 3 tracks
    Ok            a/b/b/x.mkv -> a/b/x.mkv
    SkippedClean  a/b/x.mkv   -> -
  source a/b/x.mkv after : 061bffdb…  46374 bytes, 2 tracks
  >>> SOURCE FILE WAS MODIFIED
```

Note the second line: `a/b/x.mkv` had already been replaced by the time it was identified, so
it was reported `SkippedClean`. The batch ended `ok=1 skipped=1 failed=0` — a clean bill of
health while a source file was eaten. Under Auto rename the source survived but outputs were
written into the input tree, which the next scan then ingests as sources.

This broke an invariant asserted in three places (README twice, CLAUDE.md §0/§4, and the
`BatchRunner` class doc). The code matched the spec literally — §4 only ever said "refuse an
output root inside the input root" — so the spec was the incomplete part.

**Fix:** `EnsureValidRoots` refuses overlap in either direction with a distinct message per
direction, and `IsInsideOrEqual` now resolves a symlink/junction on the final path component so
two names for one directory compare equal. A link *mid*-path is still not followed; .NET
exposes no full canonicalization, and that limitation is documented on the method.

**Verified:** the scenario above is now `REFUSED: The input folder must not be inside the
output folder…` with the source intact at 3 tracks. Covered by
`RefusesAnInputRootInsideTheOutputRoot`, `RefusesRootsThatAreTheSameDirectoryReachedThroughASymlink`,
`TheDangerousMappingThatGuardExistsForIsRealWhenUnguarded` (which pins the mapping the guard
exists for, so the guard can't be silently removed), and a `BatchRunner`-level equivalent.

### 2. Medium — a zero-match subtitle filter discarded the whole file *(fixed)*

`TrackPlan.Create` treated either filter matching zero tracks as a whole-file `SkipNoMatch`:

| audio filter | subtitle filter | old outcome | old output |
|---|---|---|---|
| `eng` | `eng` | `SkippedNoMatch` | none |
| `eng` | `ger` | `Ok` | `movie.mkv` |

The §4 rationale is "don't emit a silent file". That is sound for audio and does not transfer
to subtitles — a file without subtitles is perfectly usable, and dropping them all is literally
what the filter asked for. The cost was high: the most natural filter pair, `eng`/`eng`,
produced **nothing at all** for every file in a mixed library lacking English subtitles, and
also threw away the audio filtering that had matched.

**Fix:** audio zero-match still blocks. Subtitle zero-match becomes `TrackPlan.Advisory`; the
remux proceeds with subtitles dropped and the result is `OkWithWarnings`, so the log still
reports it. Spec §4 revised to match.

**Verified:** the same file now yields
`OkWithWarnings movie.mkv — subtitle filter matched none of this file's subtitle tracks — all
subtitles dropped`, with output tracks `[video, audio eng]`. The audio safety rule is unchanged
— a `kor` audio filter still gives `SkippedNoMatch` and no output.

### 3. Low — no cleanup for orphaned working files *(fixed)*

The v0.1.3 commit protocol is correct, but a crash or power loss left
`<dest>.<token>.mkvhippo-tmp` in the output tree permanently — nothing swept them, while the
README called the writes "crash-safe" without the caveat.

**Fix:** `MkvRemuxer.SweepWorkingFiles` runs at batch start over the output tree and reports a
`WorkingFilesSweptEvent`. A file another instance is actively muxing is locked on Windows, so
its delete fails and it is left alone — the sweep is safe against a concurrent second instance.

**Verified:** two planted leftovers (one nested) swept, real outputs untouched.

### 4. Low — auto-scan identified every file twice *(fixed)*

Measured: 3 files → `scan=3 + run=3 = 6` `mkvmerge -J` calls. It followed from the §5 auto-scan
feature and doubled the header-read pass on large libraries.

**Fix:** `ScanReport.Identified` carries the per-file results; `RunAsync` takes an optional
`preIdentified`. `MainForm` passes it **only** for a scan that just ran — an older scan
(`HasScanned` true) may predate edits to the tree, so that path still identifies fresh. The
parameter documents that it is trusted as-is.

**Verified:** now `scan=3 + run=0`. Both directions covered
(`AScanImmediatelyBeforeARunSparesEveryFileASecondIdentify`,
`WithoutAPreIdentifiedReportEveryFileIsIdentifiedFresh`).

### 5. Low — PDH P/Invokes allowed DLL hijacking *(fixed)*

Five `pdh.dll` imports in `ResourceMonitor` lacked `DefaultDllImportSearchPaths` (CA5392). The
executable's own directory precedes System32 in the load order, and the README actively tells
users to drop `mkvmerge.exe` beside `MKVHippo.exe` — so "a folder the user assembled by hand"
is the expected deployment, and a planted `pdh.dll` there would load first.

**Fix:** every import pinned with `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`.
**Verified:** CA5392 count under `AnalysisLevel=latest-all` went 10 → 0.

### 6. Low — vulnerable transitive test dependencies *(fixed)*

`System.Net.Http 4.3.0` and `System.Text.RegularExpressions 4.3.0`, both High severity, pulled
transitively via `Microsoft.NET.Test.Sdk 17.8.0`. Confirmed **absent from the shipped exe**
(the `MkvHippo.App` closure had zero matches), so user risk was nil — build-time hygiene only.

**Fix:** Test.Sdk 17.8.0 → 17.14.1, xunit 2.5.3 → 2.9.3, runner 2.5.3 → 3.1.1, coverlet
6.0.0 → 6.0.4. `dotnet list package --vulnerable --include-transitive` is now clean for all
three projects.

**Deliberately not done:** `xunit` 2.x is flagged `Legacy` in favour of xunit.v3. 2.9.3 is the
latest supported v2 and is not vulnerable; moving to v3 is an API migration, not a version
bump, so it is deferred rather than rushed into a fix pass.

The bump immediately earned its keep: a new analyzer (xUnit2031) caught a latent
`Assert.Single(… .Where(…))` in the v0.1.3 collision test, now corrected.

### 7. Low — no CI *(fixed)*

A well-tested engine with nothing running the tests automatically.

**Fix:** `.github/workflows/ci.yml` with three jobs — engine tests on Linux (`-warnaserror`),
an `-warnaserror` app build on Windows, and a dependency audit that fails the build on any
vulnerable package. All three commands were run locally first; the audit's detector was
checked against a known-bad line so it can't silently pass.

### 8. Low — `dist/` holds a local `mkvmerge.exe` *(fixed, documentation)*

It is gitignored so it cannot be committed, and the routine names `dist/MKVHippo.exe`
explicitly — but a globbed `gh release create … dist/*` would upload a 22 MB `mkvmerge.exe`,
which is exactly the bundling §2 forbids on licensing grounds.

**Fix:** the release routine in CLAUDE.md now states the asset must be named explicitly and
says why.

### 9–11. Documentation *(fixed)*

- **CLAUDE.md contradicted itself:** line 205 said "v0.1.2 (current)" beside a v0.1.3 section
  and a `0.1.3` csproj; "111 tests green" sat beside "124 tests green". The §8 acceptance
  checklist was still entirely unticked despite being satisfied. The §1 tree said `mkv-hippo/`
  (actual: `mkv_hippo`) and omitted eight files that exist. Fixed; the evergreen intro no
  longer hardcodes a test count, since that is what drifted.
- **README claimed what finding 1 falsified** — "never touching your source files" and "Sources
  are never modified". Both now hold, and the text says *why* (non-overlapping roots). The
  usage step no longer states only half the rule.
- **`CODE_REVIEW.md` was stale** — it described the five v0.1.2 findings, all long fixed. This
  document replaces it.

---

## Not changed, and why

- **Broad `catch (Exception)` in `BatchRunner`, `MainForm` and `AdaptiveScheduler`** (CA1031,
  14 sites). Deliberate: a batch must survive one bad file, and the exception text is reported
  per-file. Narrowing these would make the tool more fragile, not safer.
- **`CA1062` null-guards on public engine methods** (18 sites). `MkvHippo.Core` is consumed
  only by the shell in this repo; defensive argument checks on every entry point would be
  noise.
- **`ConfigureAwait` in the WinForms layer** (CA2007). The UI *wants* the synchronization
  context — adding `ConfigureAwait(false)` there would be a bug.
- **Long-path (>260 char) behaviour.** No `app.manifest` declares `longPathAware`. Deep media
  trees can plausibly exceed MAX_PATH, but this cannot be tested from Linux and .NET's own
  path handling may already cover it. Left alone rather than changed blind — worth one test on
  a real Windows box.
- **Log growth.** `txtLog` is unbounded; a six-figure batch would bloat it. Not worth a ring
  buffer at this tool's scale.

## What holds up well

Stated because it is true and worth preserving:

- **The Core/App split is real, not nominal.** The entire engine is exercisable on Linux, which
  is the only reason a Windows-only app can carry 136 meaningful tests.
- **Concurrency is carefully done.** Checked specifically: lock ordering is consistent with no
  nesting, the `_idle` completion race in `AdaptiveScheduler` is sound in both interleavings,
  and the `collisions` dictionary is fully populated before dispatch and never mutated, so the
  concurrent reads are safe.
- **The scheduler tests are the best thing in the repo** — controlled jobs with explicit
  release, positive assertions under a deadline, negative assertions after a settle window, and
  exact `ActiveCount` checks. They test the tricky scale-down drain semantics properly rather
  than approximating them.
- **§2's "no text-scraping" rule is honoured everywhere:** JSON identify and exit codes only.
- **Privacy posture verified:** a single author identity across all 41 commits, `PathMap` in
  effect, and a clean, logical history.

## Verification method

Engine changes are covered by the unit suite. Everything touching the filesystem was also run
end to end against real mkvmerge v82 on Linux, driving `MkvHippo.Core` through a throwaway
harness over generated MKV/MP4 fixtures: overlapping roots (refused, source intact), subtitle
zero-match (file produced, subtitles dropped), audio zero-match (still skipped), leftover
sweep (two removed, outputs untouched), and scan reuse (`run=0` identifies).

The WinForms shell is compile-only here — `OnFormClosing`, the gauges and `ResourceMonitor`
cannot be exercised from Linux and still want a pass on a real Windows box.
