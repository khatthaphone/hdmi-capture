# ADR 0005 — Windows startup: ReadyToRun, not Native AOT (for now)

- **Status:** accepted (2026-10-22)
- **Context:** Spec §13.10 asks for a live picture less than 1 s after a cold start. Task 101 step 3 says to try Native AOT with trimming and keep it only if its warnings are clean and nothing breaks. The app is .NET 10 + WinUI 3 (Windows App SDK 2.5, self-contained). Media interop uses CsWin32 (classic `[ComImport]` marshalling) and Vortice.

## Measurements (dev PC, unpackaged self-contained x64, `-testPattern smpteBars`)

Time from process start to main window created, median of 5 warm runs (`startup.ps1`, the first run is discarded):

| Build | Median | Runs (ms) |
|---|---|---|
| Debug (JIT) | 590 ms | 697, 590, 562, 593, 545 |
| Release, JIT | 608 ms | 543, 557, 621, 655, 608 |
| Release, ReadyToRun | **466 ms** | 467, 466, 450, 482, 451 |
| Release, Native AOT | doesn't build | 8 × IL2050 (see below) |

Opening the card adds about 100–150 ms from Connecting to Live (`[capture]` log). In Debug builds the log shows 720–1010 ms from process start to live picture. ReadyToRun saves about 140 ms of that.

## Decision

1. Release builds (the MSIX) use **ReadyToRun** (`PublishReadyToRun=true`, already set in `HDMICaptain.App.csproj`).
2. **Native AOT is not adopted.** `PublishAot=true` fails with IL2050 at every CsWin32 P/Invoke that takes a COM interface: `MFCreateAttributes`, `MFCreateDeviceSource`, `MFCreateSourceReaderFromMediaSource`. AOT has no built-in COM interop, so these would also fail at run time. Making the app AOT-clean would mean regenerating the MF interop with COM source generators (`[GeneratedComInterface]`/ComWrappers, or CsWin32 with marshalling off and raw pointers). Vortice and the WinUI projections would then also need checking.
3. Revisit if cold start on slower hardware (an Arm laptop or an HDD) misses the 1 s target with ReadyToRun, or when CsWin32's COM source-generator mode is mature enough to switch with little work.

## Consequences

- The MSIX is about 48 MB larger (283 MB vs 235 MB published) because of the precompiled code. Trimming is still off.
- The interop stays as it is. New interop should prefer `[GeneratedComInterface]`/`[GeneratedComClass]` (as the source-reader callback already does) so a later AOT move gets smaller rather than larger.
