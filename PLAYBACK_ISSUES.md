# JumpSolver — Playback Issues Living Document

Last updated: 2026-03-25

---

## Current Status

Best run: **5/5 jumps in Example1** (all jumps fired, all waypoints reached).
Latest: Example12 diagnosed (2026-03-25) — jumps 1–7 fired successfully (jumps 3–7 within 0.06 yalm XZ error). Jump 7 is a pure left-strafe jump (`SumForward=0, SumLeft=1.0`); after it fired, the `airborneInjFwd` fallback incorrectly set InjectedForward=1.0 throughout the flight arc, pushing the character diagonally off the platform. Fixed (#20).

---

## Problems & Attempts

### 1. Standing jump (zero horizontal momentum)
**Symptom:** Character jumps straight up instead of forward.
**Root cause:** `ClearInjection()` was called in Framework.Update before the RMI hook fired that frame. FFXIV has no momentum — RMI input IS the forward force every frame including mid-air.
**Fix (DONE):** Enter `WpWaitLand` state after every jump. Store the jump frame's injection values (`airborneInjFwd/Left/Facing`), inject every tick until landing is confirmed (5 stable Y frames), then clear.

---

### 2. Jump detection fires 1–3 frames late
**Symptom:** Jump fires slightly after the correct moment in the run-up, causing short/wrong arcs.
**Root cause:** Old detection watched for first rising Y frame — but the spacebar was pressed 1–3 frames before Y starts rising.
**Fix (DONE):** Record spacebar edge (`keySpace && !prevKeySpace`) in `JumpRecorder.Tick()`. Requires re-recording.

---

### 3. Double jump detection during recording
**Symptom:** A single jump was marked as two jumps in the recording.
**Root cause:** Y-delta detection could re-trigger during the landing phase.
**Fix (DONE):** Spacebar edge detection (same fix as #2) — can't double-fire.

---

### 4. Speed measurement fluctuating wildly
**Symptom:** `SpeedXZ` in InputMonitor jumped between 0 and 8+ every frame.
**Root cause sequence:**
- First attempt: per-frame delta division → 0/spike aliasing because FFXIV position updates at ~30Hz but Framework.Update runs at 60Hz+.
- Second attempt: EMA smoothing → still oscillated (alpha too low).
- Third attempt: rolling 500ms window (oldest→current) → still blipped; window boundary shift caused discrete jumps when oldest sample was evicted.
**Fix (DONE):** Rolling 750ms window with **running path-length sum** (segment distances, not start→end displacement). Only enqueue when position actually changes. Boundary evictions subtract their segment contribution cleanly.
**Result:** Holds steady ~6.08–6.12 yalms/s on straight run. Turning causes real variation (expected).

---

### 5. HTTP thread IPC deadlock risk (game freeze)
**Symptom:** Game froze completely, couldn't kill via Task Manager.
**Root cause:** `ClaudeAccessXIV` `GET /player/inputs` called `_jsInputsSub.InvokeFunc()` from the HTTP background thread, which serialized `InputMonitor.Snapshot` — potential race with the framework thread writing those properties.
**Fix (DONE):** Pre-serialize `InputMonitor.Snapshot` to `_lastInputJson` on the framework thread every tick. IPC func returns the cached string — immutable, thread-safe.

---

### 6. UseAction rejected silently — character doesn't jump
**Symptom:** Jump frame is reached during playback, `FireJump()` returns false, execution falls through, jump is skipped entirely.
**Root cause:** `ActionManager->UseAction` occasionally rejects the jump (animation lock, recast cooldown, other game state).
**Fix (DONE):** Added `pendingJump` retry flag. Holds on the jump frame and retries every tick. Retry window: **120 frames (~2 seconds)** before giving up.
**Status:** Better but still occasionally fails. 2-second window may still not be enough in some cases.

---

### 7. Last jump falls straight down (no airborne injection)
**Symptom:** Character makes it through all intermediate jumps but the last one has no forward momentum.
**Root cause:** `WpWaitLand` was only entered when `waypointIndex < waypoints.Count - 1`. The last jump skipped it entirely. Next playback frames after the jump had `SumForward=0`, triggering `ClearInjection()` mid-air.
**Fix (DONE):** Always enter `WpWaitLand` for every jump. On landing, check if `waypointIndex >= waypoints.Count` — if so, resume playing (`FrameIndex++`, `State = Playing`) instead of navigating.

---

### 8. XZ position snapping during playback
**Symptom:** Character visibly snapped/teleported during playback. User confirmed it felt unnatural.
**Root cause:** `NudgeXZ` per-frame correction fought against the RMI injection. The correction pulled toward the recorded position while SumForward pushed forward — visible conflict.
**Fix (DONE):** Removed `NudgeXZ` from `TickPlayback`. Waypoint navigation already resyncs position between jumps; per-frame correction was redundant.
**Note:** `NudgeXZ` method still exists in `MoveHook` — could be repurposed later.

---

### 9. Camera replay causing facing snaps up to 146° — character runs off ledge
**Symptom:** During playback, camera snapped to a new angle, character turned and ran off the edge.
**Root cause (suspected):** Recorded camera angles crossed the ±π wrap boundary (e.g., DirH going from ~3.13 to ~-3.13 as player spun around). During replay, this appeared as an instant ~360° rotation. The game may have used the sudden camera delta to steer the character before the facing injection corrected it.
**Root cause confirmed by diag CSV analysis:** Playback had 9 facing snaps of 33–146° correlating with Injecting=0 frames. FFXIV auto-turns the character toward camera `DirH` after the RMI hook. We were replaying recorded camera yaw (which differed from character facing) — the game's auto-turn then fought our `SetPlayerFacing` and overwrote `obj->Rotation`.
**Fix (DONE):** Camera yaw is locked to `frame.Facing` (character facing) during playback. Game auto-turn now always agrees with injected facing. Camera pitch still replays recorded value (cosmetic, doesn't affect movement).

---

### 10. Camera not replaying at all
**Symptom:** Camera doesn't follow recorded movement during playback.
**Root cause:** Per-frame camera replay was removed in fix #9 first attempt.
**Fix (DONE):** Re-enabled with wrap protection (see #9 second fix).
**Status:** Not yet tested.

---

## Architecture Notes

- **Framework.Update fires BEFORE RMI hook.** Peak RMI values in InputMonitor are from the *previous* frame's hook calls. `ResetNaturalPeaks()` must be called at the END of OnUpdate, after all reads.
- **HandleMoveInput (RMI) fires multiple times per Framework.Update.** Use `PeakNatural*` not `LastNatural*` for stable readings.
- **FFXIV position updates at ~30Hz**, not every render frame. Speed measurement and landing detection must account for this.
- **`obj->Rotation` = character world-space facing** — controls movement direction. Camera is cosmetic at the physics level (we believe).
- **`0xE0000000` = self-target sentinel** for `UseAction`.
- **Dalamud dev plugins hot-reload** when the DLL changes — Debug build only (Release doesn't trigger the copy target).
- **Build command:** `dotnet build -c Debug` from `JumpSolver/src/`.

---

### 11. Camera snapping 49–93° at each waypoint transition

**Symptom:** Visible camera jerk at every WpNav→Playing transition. Character sometimes veers off course at that moment.

**Root cause (confirmed by CSV analysis, 2026-03-23):** During `WpWaitLand` and `WpNav`, `SetCameraAngles()` was never called. Camera DirH stayed frozen at the last `Playing` frame's value. When playback resumed, `TickPlayback` immediately wrote `navFacing + π` — a single-frame jump equal to the facing delta between the old run-up and the new one (49–93° in the test run).

CSV pattern: every snap frame had `Injecting=0`, `InjFwd=0` — the 1-frame gap between WpNav arrival and the first TickPlayback tick.

**Fix (DONE 2026-03-23):**
1. `TickWpWaitLand` — set camera to `airborneFacing + π` every tick (camera stays behind character during flight).
2. `TickWpNav` — use `ApplyCamAngle` to smoothly sweep camera toward `navFacing + π` each tick.
3. WpNav arrival — immediately hard-set camera to `navFacing + π` before transitioning to Playing, so TickPlayback's first write is a no-op.

**Status:** Built, not yet tested.

---

---

### 12. Facing snap at WpNav start (~1–1.7 rad every waypoint transition)

**Symptom:** Even with camera snap fixed (#11), the character's body snapped sharply on the first frame of WpNav. CSV confirmed: WpWaitLand landing frame had `face=1.977`, WpNav frame 1 had `face=0.248` — a 1.73 rad instant snap.

**Root cause:** `TickWpNav` calls `SetPlayerFacing(Atan2(toward waypoint))` unconditionally on frame 1. The character was still at `airborneFacing` (where the jump was launched), which could be far from the waypoint direction.

**Fix (DONE 2026-03-23):** Added a new `WpRotate` state between `WpWaitLand` and `WpNav`. On landing detection:
1. Compute `rotTargetFacing = Atan2(navTarget - playerPos)`.
2. Set `rotBlendFacing = airborneFacing`.
3. Enter `WpRotate`. Each tick: step `rotBlendFacing` toward `rotTargetFacing` by at most `MaxCamDelta` (0.3 rad/frame). Camera kept behind character. No movement injection (character stands still).
4. Once within `RotateDoneEps` (0.05 rad), snap to target and enter `WpNav`.

Worst-case rotation: 1.75 rad / 0.3 rad·frame⁻¹ ≈ 6 frames ≈ 100ms. Invisible at normal playback speed.

**Status:** Built, not yet tested.

---

### 13. Landing detected at jump apex — forward injection killed mid-air

**Symptom:** Character fires the jump, rises, then loses all forward momentum at the apex and falls straight down. First jump failed every time in Example2.

**Root cause:** `LandingYEps = 0.005f` is bigger than the Y delta at the jump apex. At the apex (Y ≈ −12.19 vs ground Y ≈ −14.0), position changes by only 0.0001–0.004 yalms per frame for several frames — well within the 0.005 threshold. The 5-frame stability check fired mid-air, `ClearInjection()` was called, character lost forward momentum and dropped short of the platform.

CSV evidence (Example2 playback):
- Jump fired at t=2677ms (Y=−14.000, apex at Y≈−12.197)
- t=3058: Injecting=1, Y=−12.1974
- t=3063: **Injecting=0** — false landing at Y=−12.1973 (apex, yDelta=0.0001)
- WpRotate started: facing rotated from 0.2001 → −2.47 while character was still in the air

**Fix (DONE 2026-03-23):** Added `hasDescended` flag. Set to true when `playerPos.Y < lastY - LandingYEps` (character has actually dropped by more than epsilon). Stable-frame counting now only begins after `hasDescended` is true. The apex has barely-moving Y that is still going *upward* or plateauing — not descending — so it never counts.

**Status:** Built, not yet tested.

---

---

### 14. Jump arcs miss platform — character fires from wrong XZ position / wrong Y level

**Symptom (Example3):** Jump 3 fired from X=0.159 but recording had X≈-0.31 — 0.47 yalm off; character missed the platform.
**Symptom (Example4):** Jump fired from Y=-14.0 instead of Y=-13.5 (step platform). Character made the first 2 recorded jumps but UseAction was silently rejected (step-up animation lock), retries timed out, Playing advanced past both skipped jump frames, and the third recorded jump fired from the wrong position/elevation.

**Root cause (XZ error, Example3):** Old `BuildWaypoints` navigated to the run-up start position (computed by walking backward in the recording). If that position computation was off, the entire run-up started from the wrong XZ, leaving the character displaced at the jump frame.

**Root cause (Y error, Example4):** Navigating directly to the jump frame's XZ (the fix for Example3) worked when the jump was at ground level but broke when the jump was on a raised step platform. Straight-line WpNav walked the character off the platform edge (Y: -13.5 → -14.0). Character then arrived at the jump XZ at Y=-14.0 (wrong level). UseAction was also being silently rejected for the full 2-second retry window because the step-up animation lock hadn't cleared when the jump frame was first reached during initial Playing.

**Fix (DONE 2026-03-23, v2):**
- Added `PosY` to `RecordedFrame` (persists world-space Y; old saves default to 0 → no step detection, safe fallback).
- `BuildWaypoints` now scans backward from each jump frame looking for a significant Y step (|ΔY| > 0.3 yalm between consecutive recorded frames). If found, the nav target is set to `PreStepPad=8` frames before that step.
- If no step found in the search window (`MaxRunUpSearch=90` frames), navigate to that many frames before the jump (bounded run-up with minimal fps-drift).
- `TryStart` now navigates to `waypoints[0]` first via WpNav (same path as subsequent jumps) instead of starting Playing from frame 0. This ensures jump 0 also benefits from the pre-step run-up approach.

**Status:** Built, not yet tested. Re-recording required for PosY to be stored (old recordings fall back to a fixed 90-frame run-up).

---

### 15. Camera spinning ~1030°/s at every waypoint transition

**Symptom (Example5, 2026-03-23):** After each landing, the camera spun visibly fast for ~10 frames while the character stood still. CSV confirmed: rows 480–489 showed exactly +0.3 rad/frame (MaxCamDelta at the time), lasting 3 rad total — a full ~172° spin visible in-game. Same pattern at rows 698–707.

**Root cause:** `WpRotate` state was designed to pre-rotate the character body before WpNav so there was no facing snap at nav start. It used `MaxCamDelta = 0.3f` (17°/frame, 1030°/s at 60fps) to step the camera, which looked fine in isolation but was highly visible when the character was standing still during rotation.

**Fix (DONE 2026-03-23):**
1. Removed `WpRotate` state entirely from `PlayState` enum and all dispatch/method code.
2. Reduced `MaxCamDelta` from `0.3f` to `0.08f` (≈4.6°/frame, ≈288°/s) — smooth but still fast enough to align within ~20 frames.
3. `TickPlayback` now uses `ApplyCamAngle(curYaw, behindYaw)` instead of a direct write — camera rate-limits itself to MaxCamDelta even during Playing.
4. `TickWpNav` arrival no longer hard-sets camera — `TickPlayback` sweeps it in on the first tick of the new Playing segment.
5. `TickWpWaitLand` continues to call `ApplyCamAngle` each tick to keep camera behind character during flight.

The facing snap that WpRotate was solving (#12) is now handled implicitly: `navFacing` is set to match the recorded run-up direction, so the character body already faces roughly the right way; the remaining delta is swept in by `ApplyCamAngle` during the first frames of Playing.

**Status:** Built (clean), not yet tested.

---

---

### 16. Jump frames skipped when recording fps > playback fps — jump never fires

**Symptom (Example6, 2026-03-23):** All 5 jumps fired on jumps 1–4. Jump 5 (frame 956 in recording) never fired. Character ran past the jump position, went off the platform edge, fell to ground, and stayed frozen. DiagCapture showed InjFwd=1.0 through the entire run-up to jump 5 with no JumpFired event.

**Root cause:** Recording captured at ~150fps (DeltaMs=5–9ms per frame). Playback runs at ~60fps (~16.7ms per game tick). The frame advance `while` loop in `TickPlayback` runs until `frameTimer < frames[FrameIndex].DeltaMs`. At 60fps, one game tick can accumulate enough time to advance through 2–3 recording frames. When this consumes a jump frame (e.g., frames 954→955→956→957 all in one tick), frame 956's `frame.Jump = true` check never runs — the jump is silently skipped. The character then kept running beyond the jump point, reached the platform edge, and fell.

**Evidence from Example6 playback CSV:**
- WpNav correctly navigated to jump 5 run-up position; Playing started at frame 866
- Frames 943–955: SumFwd=1.0 (run-up), InjFwd=1.0 in CSV — character running correctly
- Frame 956: jump should fire here (from PosX≈-3.90, Y≈-7.44) — but it was consumed by the while loop in the same tick as frames 954 and 955
- After ~180ms of running (91ms past where the jump should have fired), the character reached the platform edge and fell to Y=-13.5

**Fix (DONE 2026-03-23):** Added a `break` at the end of the while loop body when the NEW `FrameIndex` (after incrementing) points to a jump frame:
```csharp
while (FrameIndex < playbackFrames.Count && frameTimer >= playbackFrames[FrameIndex].DeltaMs)
{
    frameTimer -= playbackFrames[FrameIndex].DeltaMs;
    FrameIndex++;
    if (FrameIndex < playbackFrames.Count && playbackFrames[FrameIndex].Jump) break;
}
```
This ensures jump frames are always processed on their own game tick rather than being consumed silently by multi-frame advances.

**Status:** Built (clean), not yet tested.

---

### 17. False landing detected mid-air when character clips near ledge

**Symptom (Example7, 2026-03-23):** Jump 4 clips the near face of platform 4 at Z≈-16.19 (short-landing due to 0.024-yalm X drift accumulated through WalkDoneThreshold). `WpWaitLand` triggers false landing at Z≈-16.19 (wrong Y level). WpNav resumes from that lip and immediately walks the character off the edge, falling to Y≈-13.96. Jump 5 fires from ground, can't reach platform 5, character dies.

**Root cause:** `TickWpWaitLand` set `hasDescended = true` inside the `landingMinFrames > 0` early-return block. A 0.007-yalm Y jitter ~47ms after `UseAction` (initial jump physics frame) fired `descFrame=true`, permanently arming `hasDescended` before the character was meaningfully airborne. Later, when the character clipped the near lip at Z≈-16.19 (Y stepped up then stabilized), 5 consecutive stable-Y frames with `hasDescended=true` triggered false landing.

Note: jumping while falling is not a valid recovery — FFXIV rejects `UseAction` for jump when the character is not grounded.

**Fix (DONE 2026-03-23):** Removed `if (descFrame) hasDescended = true;` from the `landingMinFrames > 0` block. `hasDescended` is now only set *after* the minimum wait expires. Post-jump jitter in the first 30 frames no longer arms it. If the character clips the lip (Y goes up = not a descent), `hasDescended` stays false, stable frames never accumulate. When forward injection carries the character forward and it steps *down* onto the main platform, that real descent arms `hasDescended` and the 5 stable frames correctly detect landing.

**Status:** Built (clean), not yet tested.

---

### 18. Jump arc misses platform because recording has mid-air steering (Facing changes during flight)

**Symptom (Example8, 2026-03-23):** 8-jump puzzle. Jumps 1–5 fire with <0.5-yalm positional error. Jump 6 fires from the correct XZ position (0.154 yalm X error) but peaks at X=-1.561, Z=-19.683 — too far right of platform 6 (which is at X≈-3.1, Z≈-20.0). Character falls back to platform 5 (launch platform). WpWaitLand detects false landing (returned to Y=-5.834 = platform 5). WpNav navigates toward platform 6's navTarget from platform 5; character walks off the edge and falls. Jump 7 fires from Y=-7.693 (void). Jump 8 never fires. Character falls to ground floor.

**Root cause:** The recording captures the player steering mid-air by rotating the camera during the J6 flight arc. Facing rotates from -2.349 (at jump fire) to -1.751 (at peak, ~34 degrees) in the recording. This curves the trajectory from the initial direction toward platform 6. In `TickWpWaitLand`, the code held `airborneFacing` fixed at the jump-fire value and called `SetPlayerFacing(airborneFacing)` every tick, injecting in a straight line throughout the arc. The playback arc went straight instead of curving, landing 0.3 yalm short of platform 6's near edge.

**Fix (DONE 2026-03-23):** In `TickWpWaitLand`, instead of holding the fixed `airborneFacing`, step through recording frames using `airFrameIndex`/`airFrameTimer` (same DeltaMs-based timing as `TickPlayback`). Each WpWaitLand tick advances `airFrameIndex` and calls `SetPlayerFacing(playbackFrames[airFrameIndex].Facing)`. This replicates the player's mid-air steering exactly. `airborneFacing` is still used for the camera-alignment `SetCameraAngles` call (cosmetic only — keeps the camera roughly behind during flight).

**Status:** Built (clean), not yet tested.

---

### 19. Nav target lands on mid-run position; 90 frames of standstill replayed unnecessarily

**Symptom (Example10, 2026-03-25):** All 8 jumps complete but fire positions vary significantly run-to-run. PB1 vs PB2 comparison: J7 error 0.437 vs 0.650 yalm, J8 error 0.355 vs 1.043 yalm, J6 error 0.001 vs 1.385 yalm (cascade from prior drift). Character visibly pauses on each platform after landing — standing still for 1–3 seconds before the run-up begins. This is standstill replay overhead.

**Root cause:** `BuildWaypoints` used a fixed 90-frame lookback (`navIdx = jumpIdx - 90`) when no Y-step was found. Players typically record: **approach run → standstill (2–8 s) → short run-up → jump**. The 90-frame fallback frequently placed `navIdx` inside the approach run, well before the standstill. Two consequences:
1. **Wrong nav target** — WpNav navigated to a mid-run position 0.2–1.1 yalm from the actual resting position (worst case: J7, 1.058 yalm off). Any WpNav arrival error was amplified by the long replay that followed.
2. **Standstill replay** — `navResumeFrame = navIdx` made Playing replay 40–120 standstill frames (SumForward=0) after WpNav arrived, causing the visible on-platform pause. The character stood still at whatever XZ WpNav reached (within 0.05-yalm tolerance), carrying that arrival offset through the run-up.

**Fix (DONE 2026-03-25):** Standstill-aware `BuildWaypoints`. After computing initial `navIdx` (Y-step detection or 90-frame fallback, unchanged), scan **backward** from `jumpIdx - 1` to find the last pre-jump standstill block (≥ `MinStandstillFrames = 10` consecutive frames with `SumForward=0` AND `SumLeft=0`). If found and no Y-step exists:
- `navIdx = stillStart` — navigate to the stable resting position where the player stood
- `navResumeFrame = stillEnd + 1` — resume Playing at the first run-up frame, skipping the standstill entirely

Y-step cases are excluded: the full replay from the pre-step position must include the step traversal so the step-up animation lock clears before `UseAction` fires.

**Results on Example10:**
- J7 navTarget error: 1.058 yalm → **0.000 yalm** (standstill position)
- J5 navTarget error: 0.339 yalm → **0.000 yalm**
- J2, J3, J8: similar improvements
- Run-up length: 90 frames → **13–21 frames** for all non-Y-step jumps
- Platform pause: eliminated

**Status:** Built (clean), not yet tested with new logs.

---

### 20. Pure-strafe jump injects spurious forward force throughout flight arc — character overshoots platform

**Symptom (Example12, 2026-03-25):** 8-jump puzzle. Jumps 1–7 fire. Jump 7 is recorded as a pure left-strafe jump (`SumForward=0, SumLeft=1.0`). After jump 7 fires, the character diverges diagonally forward-left instead of arcing left, overshoots the jump 8 platform, falls off the puzzle (~2.8 yalm off target), and WpNav for jump 8 routes to ground floor (Y=-13.91) because the character is already fallen. Playback permanently halts. Jump 8 through 17 never fire.

**Root cause:** In `TickPlayback`, when a jump frame is processed:
```csharp
airborneInjFwd = frame.SumForward != 0f ? frame.SumForward : (frame.Moving ? 1f : 0f);
```
The intent of the `frame.Moving ? 1f : 0f` fallback is to support old recordings that only stored a binary `Moving` flag. However the condition `frame.SumForward != 0f` is too narrow: if `SumForward=0` but `SumLeft=1.0` (pure strafe), the character **is** moving (so `frame.Moving = true`), and the fallback fires — setting `airborneInjFwd = 1.0f` when it should be `0.0f`. `WpWaitLand` then injects `InjectedForward = 1.0` throughout the entire flight arc, adding full forward thrust to a jump that should have zero forward component.

**Fix (DONE 2026-03-25):** Widen the modern-recording guard to check either axis:
```csharp
airborneInjFwd = (frame.SumForward != 0f || frame.SumLeft != 0f)
                 ? frame.SumForward
                 : (frame.Moving ? 1f : 0f);
```
If either `SumForward` or `SumLeft` is non-zero, the recording has valid RMI float data — `SumForward` is trusted directly even when zero (pure strafe). The `Moving` fallback only fires when both are zero, i.e., a genuinely old binary-only recording.

**Status:** Built, not yet tested.

---

### 21. WpNav straight-line walk clips platform edge — character falls off ledge mid-navigation

**Symptom (Example13, 2026-03-25):** 17-jump puzzle. Jumps 1–11 fire successfully. After jump 11 lands, WpNav navigates toward the jump 12 run-up position (navTarget at roughly X=-3.8, Z=-18.26 on a narrow elevated platform). The straight-line WpNav path from the jump 11 landing spot clips the edge of the platform. Character Y begins declining ~0.006–0.014 yalms/frame (walking along the lip) then drops off at row 1751 of the playback log (X=-3.448, Y=3.234, Z=-18.309). Jump 12 fires mid-fall (Y=2.147, 1.2 yalms below correct platform), WpNav for jump 13 navigates to ground floor (Y=-13.91), and jumps 14–17 never fire.

**Root cause:** `TickWpNav` injected `InjectedForward = Clamp(dist / WalkSlowDist)` aimed directly at the nav target XZ. If the straight-line path from the landing position to the nav target crosses the platform boundary (irregular ledge geometry, narrow ledge, L-shape), the character walks off the edge. vnavmesh terrain-follows around these edges via Recast/Detour pathfinding; JumpSolver's straight-line code does not.

**Fix (DONE 2026-03-25):** WpNav now uses vnavmesh `PathfindAndMoveTo(navTarget, fly=false)` on the first tick of each WpNav phase, with straight-line injection as a fallback if vnavmesh is unavailable or reports `IsReady=false` for the current zone. When vnavmesh is active, JumpSolver clears its own injection so the two systems don't fight. Arrival is still detected by XZ distance (WalkDoneThreshold=0.05), at which point vnavmesh is stopped and Playing resumes. `NavY` was added to `JumpWaypoint` to give vnavmesh a full 3D target. The fallback preserves all existing behavior for zones without a cached navmesh.

**Status:** Built, not yet tested.

---

## Pending / Unknown

- [ ] **#21** — Test Example13: verify WpNav no longer walks character off the jump 12 platform ledge; all 17 jumps fire.
- [ ] **#20** — Test Example12: verify jump 7 pure-strafe arc is preserved, character lands on jump 8 platform, all jumps fire.
- [ ] **#19** — Test Example10 × 2 runs: verify J5/J7 fire errors drop, platform pauses gone, all 8 jumps complete.
- [ ] **#18** — Test Example8/9: verify 8-jump puzzles still complete cleanly under new BuildWaypoints.
- [ ] **#17** — Test Example7: verify character continues past near-lip clip and correctly lands on main platform surface, then jump 5 fires from platform 4.
- [ ] **#16** — Test Example6: verify jump 5 now fires correctly and all 5 jumps complete.
- [ ] **#15** — Test Example5: verify camera no longer spins at waypoint transitions and all 5 jumps still land.
- [ ] **#14 v2** — Re-record Example4 (and Example5) so PosY is captured. Verify step-detection selects the correct pre-step nav target and the character steps up naturally before each jump.
- [ ] Verify apex false-landing fix (#13) — does the character now complete the jump arc?
- [ ] Verify camera snap fix (#11) — do transitions feel smooth now?
- [ ] Why does UseAction get silently rejected (API returns true, game ignores it) for 2+ seconds after a step-up? Hypothesis: step-up animation lock. Can we detect the lock state and wait it out explicitly?
- [ ] After all recent fixes, what is the overall success rate?

### Architecture note (2026-03-23)
`TryStart` now uses `WpNav` to navigate to `waypoints[0]` before starting Playing, rather than replaying from frame 0. This means **all** jumps (including the first) now use the pre-step run-up approach. Old puzzles still work (frame 0 is used as default if waypoints[0] is at navIdx=0). The `WalkToStart` step is now less critical — the character can start `TryStart` from anywhere within NavTimeout range of the first waypoint.

### Architecture note (2026-03-25)
`BuildWaypoints` now detects pre-jump standstills and sets `navResumeFrame` to the run-up start rather than `navIdx`. For non-Y-step jumps, `navIdx` is also updated to the standstill start position. This means playback skips the standstill entirely after WpNav arrives — character walks straight into the run-up. Y-step jumps still replay from `navIdx` (pre-step) to handle terrain step-up approach naturally.
