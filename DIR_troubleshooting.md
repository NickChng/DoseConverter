# DIR Troubleshooting Log

A running, chronological narrative of the effort to make masked deformable image
registration (DIR) work for the adaptive workflow. Purpose: **stop retreading ground.**
Read this before changing `Model/DeformableRegistrationService.cs` registration logic.

Companion doc: `RegistrationAssessment.md` (design-level assessment). Where the two
disagree, **this log is newer** — see the divergence notes below.

_Last updated: 2026-06-02._

---

## The clinical goal

Deform a source image onto the patient's daily (target) image so dose can be accumulated,
**without the DIR being confused by patient accessories** (bolus, immobilisation). A bolus
sits directly on the skin and notoriously drags the patient's soft tissue outward to match
it. Strategy: define **body (true-patient) contours** on source and target, and have the
DIR ignore everything outside the body.

The user can also pick an **existing rigid registration** as a starting alignment.

## Current architecture (as actually built)

Two selectable algorithms in `DeformableRegistrationService.cs`, chosen via
`RegistrationAlgorithmType` (config + DIR tab):

| Path | Method | Mask handling | Status |
|---|---|---|---|
| **Demons** | `RunDiffeomorphicDemonsRegistration` — `DiffeomorphicDemonsRegistrationFilter`, manual multi-resolution pyramid, histogram-match + rescale | Demons has **no** native mask support → masks applied by intensity-zeroing inputs (AND) + post-field rigid/identity blend (OR) | **Works** on the full image |
| **B-spline** | `RunBSplineMaskedRegistration` — `ImageRegistrationMethod`, L-BFGS-B, B-spline transform | **True metric masks** (`SetMetricFixedMask`/`SetMetricMovingMask`) — out-of-body voxels excluded from the metric & its gradient | **Under repair** (was producing garbage) |

The B-spline path is the right tool for bolus exclusion (metric masks create no artificial
skin edge), but has been the source of the trouble.

---

## Chronological log

### Entry 0 — Misfire on a stale version (important context)
The first review in this effort was accidentally done against an **older checkout** of
`DeformableRegistrationService.cs` (~2022 lines) in which only the demons path existed and
masks were applied as a **post-hoc displacement-field mask**. Recommendations and edits
from that pass (input image masking, "switch to metric masks") were written against code
that no longer reflected reality — the working tree had since advanced to commit
`fe15601 "with metric masks"` (~2395 lines) which **already** had the metric-mask B-spline
path. Those edits never landed on the real tree (git status was clean).
**Flag:** always confirm `git log -1` / file length before reviewing this file.

### Entry 1 — Diagnosis of the B-spline "garbage"
On the real (current) code, the B-spline + metric-mask path produced garbage. Assessed
causes, ranked:
1. **Under-constrained control points at the mask boundary** (most likely primary): with
   true metric masks, B-spline control points whose support is mostly outside the body get
   little/no gradient and there is **no bending-energy regularization** → L-BFGS-B drives
   them to extreme values → "massive distortion at the edges." Architectural, not a typo.
2. **Mid-flight optimizer reconfiguration**: `SetOptimizerAsLBFGSB(...)` was being called
   from inside the `sitkMultiResolutionIterationEvent` callback during `Execute()` — not a
   supported pattern.
3. **Mattes MI for CT-CT** (unimodal): noisier / more local-minima-prone than correlation.
4. **Low metric sampling** (config 0.1 = 10%) starving a high-DOF B-spline gradient.
5. `SetOptimizerScalesFromPhysicalShift()` — unnecessary for B-spline (uniform mm params),
   and very slow on thousands of parameters.

**Composite transform order** was examined and **left unchanged** — it matches the working
demons path (deformable added first, rigid second). ⚠️ Still unverified — see Open Questions.

### Entry 2 — Fixes applied (Option 1: stabilise the B-spline path) + logging
In `RunBSplineMaskedRegistration`:
- Metric **Mattes MI → `SetMetricAsCorrelation()`** (unimodal CT-CT).
- **Removed** the mid-flight optimizer reconfiguration; configure the optimizer once.
- **Removed** `SetOptimizerScalesFromPhysicalShift()`.
- **Raised metric sampling** to ≥ 0.5 when masks are active.
- Added **diagnostics**: a one-line dump of every resolved parameter, the final metric +
  stop condition, and `LogTransformDiagnostics(...)` reporting max/mean in-body displacement
  and Jacobian min/max + folded-voxel count (Jac ≤ 0) for the deformable-only and final
  transforms.

> **Divergence from `RegistrationAssessment.md`:** that doc (§7) states the B-spline path
> intentionally uses **Mattes MI** and skips histogram normalisation. We have since switched
> the metric to **Correlation** for CT-CT robustness. Histogram normalisation is still
> (correctly) skipped. Treat §7's "Mattes MI" statement as superseded.

### Entry 3 — DEAD END: per-level B-spline grid refinement with L-BFGS-B
Tried `SetInitialTransformAsBSpline(bspline, true, scaleFactors=[1,2,4])` to refine the
control grid per pyramid level (better fit + thinner ambiguous band at the mask edge).
**Crashed entering level 2:**
```
itkObjectToObjectOptimizerBase: ITK ERROR: LBFGSBOptimizer4:
Size of scales (1536) must equal number of local parameters (6591)
```
Cause: grid refinement changes the parameter count between levels (1536 → 6591), but
L-BFGS-B's scales array is sized once at setup and is **not** resized.
**Resolution:** reverted to a **single fixed B-spline grid** (`SetInitialTransform`,
constant parameter count). A single (coarser) grid also has fewer under-constrained
boundary control points → more stable, which helps Entry 1 item #1.

> **DO NOT RETRY** per-level B-spline refinement while using `SetOptimizerAsLBFGSB`. It is
> fundamentally incompatible. It would require switching the optimizer to `LBFGS2`
> (which tolerates the changing parameter count). Only revisit if single-grid quality is
> insufficient AND you also move to LBFGS2.

### Entry 4 — Progress display off-by-one (level + iteration counts)
Symptom: progress started at "level 2/3" and iteration num/denom didn't match the config.
Causes & fixes:
- `sitkMultiResolutionIterationEvent` fires at the **start** of each level. Code started
  `levelIdx = 0` and pre-incremented, so level 0 displayed as 2/N. Fixed: start at `-1`,
  increment on each fire (first fire → level 0 → displays 1/N). Added an explicit
  "entering level k/N" log line.
- The iteration **denominator** was the per-level value but the optimizer actually runs a
  **uniform** cap. With L-BFGS-B you cannot vary the iteration cap per level (that needs
  LBFGS2), so the config's `MaxIterationsPerLevel` (e.g. `50 30 20`) now **collapses to its
  maximum**, applied to every level. The display shows `iter/<that max>`; a log line states
  the effective uniform cap. `GetOptimizerIteration()` resets to 0 at each level (per-level
  count, not cumulative).

### Entry 5 — ROOT CAUSE FOUND: inverted rigid-composition order (both paths)
The 2026-06-02 run (B-spline + demons, with a rigid start) gave the decisive diagnostics:
```
[B-spline (deformable only)]: maxDisp=15.5mm, meanDisp=1.0mm, jacDet[min=0.731,max=1.318], folded=0
[B-spline + rigid (final)]:   maxDisp=67.6mm, meanDisp=10.9mm, jacDet[min=-1.315,max=2.990], folded=62211
```
The deformable field alone is excellent (smooth, no folding). Composing with the rigid
start introduces a **negative Jacobian (−1.315) and 62,211 folded voxels** — impossible for
a correct composition (a rigid has det = 1, so rigid∘smooth stays positive). The folding
appears *only* after the rigid → the composition is malformed.

**Cause:** `CompositeTransform` was built as `AddTransform(deformable); AddTransform(rigid)`,
with a code comment claiming SimpleITK applies transforms in *insertion* order. It does
**not** — SimpleITK applies the **last-added transform first (LIFO)**. So the code computed
`deformable(rigid(x))` instead of the required `rigid(deformable(x))`. Applying the rigid
first pushes points outside the B-spline grid domain (and is geometrically wrong), producing
the fold. **This is the Open-Question composite-order caveat from earlier, now confirmed by
data.**

**Critical scope:** EVERY run in the logs used a rigid start (even the "no mask" demons run
logs "Pre-aligning moving CT with rigid registration…"), so this single bug corrupted
*all* results — both algorithms, mask or no mask. That's why "nothing was accurate
regardless of mask."

**Fix applied:** swapped the order in all three composition sites (B-spline final, demons
flatten, demons no-mask branch) to `AddTransform(rigid); AddTransform(deformable)` so the
deformable is inner (applied first) and the rigid is outer (applied last) →
`rigid(deformable(x))`. Also added `LogTransformDiagnostics` to the demons path so its final
field is now measurable too.

**How to confirm the fix on the next run:** the `[... + rigid (final)]` diagnostics should
show a **positive** Jacobian min (roughly matching the deformable-only range, ~0.7–1.3) and
**folded voxels ≈ 0**, with max in-body displacement in a physically plausible range.

> Lesson / flag: **don't trust the in-code "insertion order" comments** — SimpleITK
> CompositeTransform is LIFO (last-added applied first). The folded-voxel diagnostic is the
> fastest way to detect a bad composition.

---

## Current state (pending the next test run)

B-spline path now: single fixed grid, Correlation metric, single optimizer config, ≥0.5
sampling, full diagnostics, corrected progress display. Awaiting a run with BODY masks on
source + target to read the diagnostics.

**Cannot be built/run in the assistant environment** (needs Visual Studio, Varian DLLs over
UNC, x64, native SimpleITK). Every change is reasoned, not execution-verified — watch for
SimpleITK API surface mismatches at compile time.

## Open questions / unverified

- **Does single-grid B-spline + Correlation + metric masks actually produce good results?**
  Read the new `DIR diagnostics [...]` lines: sane in-body max displacement (order of cm,
  not tens of cm) and low/zero folded-voxel count = converging; otherwise still diverging.
- **CompositeTransform order (rigid + deformable).** RESOLVED in Entry 5 — it was inverted in
  both paths and is now fixed (rigid added first/outer, deformable second/inner). Pending
  confirmation on the next run via the folded-voxel diagnostic.
- **Grid resolution** is now the single most important quality knob (`BSplineGridNodes`):
  too fine → boundary instability returns; too coarse → under-fits real anatomy.

## Dead ends — DO NOT RETREAD

1. Per-level B-spline grid refinement (`SetInitialTransformAsBSpline` scaleFactors) with
   **L-BFGS-B** → scales/parameter-count mismatch crash (Entry 3). Needs LBFGS2.
2. Reconfiguring the optimizer mid-`Execute()` from a multi-resolution callback (Entry 1/2)
   → unsupported; removed. Don't reintroduce to get per-level iteration caps.
3. Post-hoc displacement-field masking as the *primary* bolus defence (the old demons
   approach) → leaves an edge discontinuity / doesn't stop in-optimization tissue drag.
   (Historical; see `RegistrationAssessment.md` §3.)

## Key SimpleITK constraints learned (so we stop relearning them)

- `DiffeomorphicDemonsRegistrationFilter` has **no** metric-mask support. Masks there must
  act on intensities/field. Metric masks require `ImageRegistrationMethod`.
- `LBFGSB`: parameter count must be constant across levels; scales sized once; cannot vary
  iteration cap per level; cannot refine the B-spline grid per level. `LBFGS2` is the
  alternative when per-level grid refinement is wanted.
- `ImageRegistrationMethod` multi-resolution: `numberOfIterations` is a **per-level** cap;
  `GetOptimizerIteration()` resets each level; `sitkMultiResolutionIterationEvent` fires at
  the **start** of each level.
- `CompositeTransform`: applies **last-added-first** (verify before trusting comments).

## How to read the diagnostics in the log

- `B-spline DIR parameters: ...` — every resolved knob; confirm masks present, sampling,
  grid, shrink/sigmas.
- `B-spline optimizer: ... uniform per-level iteration cap = N` — the real iteration budget.
- `B-spline entering level k/N` and `B-spline level k/N — metric ... (iter i/N)` — progress.
- `DIR diagnostics [B-spline (deformable only)]` and `[B-spline + rigid (final)]`:
  - `maxDisp(in-body)` huge → diverging.
  - `foldedVoxels(jac<=0)` large → folding/garbage.
- `LogMaskCoverage` lines — confirm both masks non-empty **before** any visual review (an
  empty mask silently disables masking; cheapest thing to rule out first).

## If still unacceptable after the next test (candidate next steps, not yet tried)

- Tune `BSplineGridNodes` first (single biggest lever now).
- Feathered / tissue-fill mask edge + small mask dilation (`RegistrationAssessment.md` §4.1).
- Wire in the existing-but-unused `CropImageToMaskBounds` to shrink the domain (§4.2).
- Only if finer grids are genuinely needed: switch optimizer to `LBFGS2` and *then* per-level
  grid refinement becomes available again.
