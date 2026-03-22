# JumpSolver — Revision History

## v0.3.0.0 — Frame Recording + Go To Start alignment
**Key milestone: first version where recording and playback actually work reliably.**

### What changed
- **Removed step mode entirely.** The previous per-step record/play approach was fragile
  (timer-based jump triggers, position bugs, three separate recording paths in the UI).
  Replaced with a single frame recording mode that captures raw RMI input values.

- **Raw RMI input recording.** `RecordedFrame` now stores `SumForward` and `SumLeft`
  (the actual float values the game reads from keyboard/gamepad each tick) instead of
  a derived `Moving` bool. Playback injects these exact values verbatim — the character
  reproduces the same acceleration, pauses, and direction changes as the original run.

- **Go To Start — proportional RMI walk (no teleport).**
  Walks the character to the recorded start point using movement injection only:
  - Faces toward target each frame via `Atan2(dx, dz)` → `GameObject->Rotation`
  - Injects `InjectedForward = Math.Clamp(dist / 1.0f, 0, 1)` — full speed when far,
    linearly decelerates within 1 yalm, stops at 0.05y
  - No phase boundaries (earlier two-phase approach caused jitter at the threshold)
  - Sets recorded start facing on arrival, then goes Idle

- **Cleaned up UI.** Single Rec / + Seg / Stop / Play flow. Diag panel hidden behind checkbox.
