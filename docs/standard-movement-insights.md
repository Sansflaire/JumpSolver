# Standard Movement Insights

Movement type: **Standard** (FFXIV character config → Movement Type → Standard)

---

## Control Mapping

| Input | Effect | RMI channel |
|-------|--------|-------------|
| W | Run forward | `sumForward` = 1.0 |
| S | Backstep (slow) | `sumForward` = -1.0 (or similar) |
| A | Rotate left on the spot | `sumTurnLeft` > 0 |
| D | Rotate right on the spot | `sumTurnLeft` < 0 |
| Q | Strafe left (face stays forward) | `sumLeft` = 1.0 |
| E | Strafe right (face stays forward) | `sumLeft` = -1.0 |
| Right-click + mouse | Rotate character precisely | Bypasses RMI — writes `obj->Rotation` directly via camera system |
| Spacebar | Jump | `ActionManager->UseAction(ActionType 5, id=2)` |

---

## Movement Speed

- Speed ramps up gradually after pressing W — not instant max speed
- **Jump trajectory depends on speed at the moment Space is pressed**
  - Tapping Space early in the run = short hop with less forward distance
  - Tapping Space after full ramp = maximum jump arc
- This ramp is handled by game physics, not by input values — `sumForward` is binary (0 or 1) the whole time
- Confirmed by diagnostic capture: natural keyboard and RMI injection both produce identical speed ramp profiles

---

## Rotation

### A/D rotation
- Goes through RMI as `sumTurnLeft`
- We record the **result** (`lp.Rotation` each frame) but not `sumTurnLeft` itself
- The result is replayed via `SetPlayerFacing` (direct `obj->Rotation` write)

### Right-click + mouse rotation
- Bypasses RMI entirely — the camera system updates `obj->Rotation` directly
- Not visible in `sumForward` or `sumLeft` at all
- Captured in recording via `Facing = lp.Rotation` each frame
- This is how Trist achieves precise facing before jumps

### Timing concern (unresolved)
- `SetPlayerFacing` is called from `OnUpdate`, which may run before or after the RMI physics tick
- If RMI fires first for a given frame, the movement for that frame uses the previous frame's facing
- This creates a one-frame lag during turns that could cause drift in multi-jump sequences
- Has not been definitively confirmed or ruled out

---

## Jump Technique

Trist's jump sequence:
1. Stop moving (or slow to the desired speed)
2. Hold right-click, rotate with mouse to precise target facing
3. Begin holding W (speed starts ramping)
4. Tap Space at the desired speed point — character leaps at an angle determined by current speed

The jump arc is therefore controlled by **two variables**:
- Facing direction at jump time (captured by `Facing` recording)
- Speed at jump time (determined by how long W was held before Space)

---

## Why Frame Replay Breaks at Multi-Jump Sequences

1. **Physics drift**: each jump produces a landing position that varies slightly from the recording due to server/client timing differences. These deltas compound across jumps.
2. **Subsequent frames assume original position**: the recorded movement for frames after landing assumes the character landed at the exact recorded position. Even small drift sends the character in the wrong direction.
3. **The second jump fails**: after jump 1, the character is at P + delta instead of P. Movement frames carry them off course. The facing may be correct but the trajectory is wrong.

---

## Implication for Playback Design

Frame-by-frame input replay is unreliable for multi-jump sequences. The right approach:
- Record naturally (frame data is good — facing, speed profile, jump positions all captured)
- Playback using **waypoints extracted from the recording** — move toward the recorded jump position, fire jump at that XZ coordinate, wait for landing, reorient, repeat
- Each jump position acts as a resync point, preventing drift from compounding

---

## Open Questions

- Does W without right-click held move toward camera direction or character facing? If toward camera, `SetPlayerFacing` alone is insufficient during W playback and we'd also need camera control or `sumTurnLeft` injection.
- How much does landing position vary between runs of the same jump? Needs measurement.
