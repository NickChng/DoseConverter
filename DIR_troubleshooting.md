# DIR Troubleshooting Log

A running, chronological narrative of the effort to make masked deformable image
registration (DIR) work for the adaptive workflow. Purpose: **stop retreading ground.**
Read this before changing `Model/DeformableRegistrationService.cs` registration logic.

Companion doc: `RegistrationAssessment.md` (design-level assessment). Where the two
disagree, **this log is newer** — see the divergence notes below.

_Last updated: 2026-06-03._

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

### Entry 6 — Composition fix confirmed; B-spline lacked the out-of-body guard
The 2026-06-02 22:xx run confirmed Entry 5's fix:
```
[B-spline + rigid (final)]: maxDisp(in-body)=67.3mm, jacDet[min=0.745, max=1.292], folded=0
```
Positive Jacobian, zero folds — the rigid composition is now correct, and demons gave a
"decent match." **But** the B-spline + body masks still pulled the bolus in, even though the
bolus is excluded from the target body contour (user double-checked).

**Why metric masks alone were not enough:** a metric mask only stops out-of-body voxels from
*driving* the optimisation. The B-spline transform is still defined **everywhere** and
**extrapolates** outside the target body, where nothing constrains it. Resampling the source
image/dose with that extrapolated field is what drags the bolus region. The in-body
displacement was tiny and clean (mean 1.0 mm, max 15 mm, no folds) — the damage was in the
*out-of-body* field, which the in-body-only diagnostic was hiding.

Notably, the **demons path already guards against this** with its "rigid-only outside body,
full field inside body" blend (Entry-0-era work). The **B-spline path had no equivalent** —
it returned the raw B-spline(+rigid) with unconstrained out-of-body extrapolation.

**Fix applied:**
1. B-spline path now applies the same body-constraining blend: convert the composite to a
   displacement field, keep the full field **inside the target (fixed) body mask**, and use
   **rigid-only outside** (identity if no rigid). Nothing outside the target body is deformed.
   (Uses the FIXED/target mask specifically — that is the contour the user confirmed excludes
   the bolus. The demons path uses the union of both masks; revisit if the source-body extent
   matters.)
2. `LogTransformDiagnostics` now reports **whole-grid** displacement as well as in-body, so the
   out-of-body extrapolation magnitude is visible. Two B-spline lines are now logged:
   `(pre-blend)` and `(final, body-constrained)`.

**How to confirm on the next run:** compare the two B-spline diagnostic lines — `(pre-blend)`
`maxDisp(whole-grid)` should be large (the extrapolation), and `(final, body-constrained)`
`maxDisp(whole-grid)` should drop to roughly the rigid-only level, with in-body unchanged.
Visually, the bolus region should no longer show pulled-in tissue.

> Open: if the bolus still appears pulled in *after* this, the artefact is **in-body** (within
> the target contour), which would point to the mask not excluding what we think, or the
> source body mask including its bolus. Check the SOURCE body contour excludes its bolus too,
> and confirm both mask coverage lines look right.

### Entry 7 — B-spline under-deforming (can't capture weight loss / contour change)
With the composition + out-of-body guard fixed, the B-spline (vs demons, no masks) was found
**not to capture large position changes** — e.g. body-contour change from weight loss. The
deformation grid showed only small smooth changes.

**Evidence:** the optimizer converges (stop reason "function tolerance reached") at a finest-
level Correlation of only **~−0.63** (good CT-CT would be ~−0.9), and the in-body field mean
was ~1 mm. It plateaus because the **5×5×5 B-spline mesh is far too coarse** (~6–7 cm
control-point spacing over a whole body) to represent cm-scale surface movement. The grid is
the representational ceiling. (Cross-level metric values are NOT comparable — Correlation is
resolution-dependent — so the per-level "resets" are expected, not regressions.)

**Fixes applied:**
1. **Increased the B-spline grid:** `BSplineGridNodes` `5 5 5` → `10 10 8` in
   `Configuration/DoseConverterConfig.xml`. This is THE knob for representational capacity.
   Finer = more local deformation captured, but slower and higher folding risk. (Verify the
   *deployed* config — the app reads its own copy; the repo value only takes effect after the
   config is redeployed/copied to the output dir.)
2. **Re-added `SetOptimizerScalesFromPhysicalShift()`** (after `SetInitialTransform`). Without
   it L-BFGS-B takes timid, poorly-scaled steps and converges to a shallow minimum
   (under-deformation). It had been removed earlier for speed; quality wins here. Scales are
   valid across levels because the single grid keeps the parameter count fixed.

**Runtime warning:** the finer grid multiplies parameters (5×5×5→~1.5k params; 10×10×8→~5.5k)
so each iteration is slower; the finest level may take noticeably longer than the ~6 min seen
before. Dial `BSplineGridNodes` back if too slow.

**How to confirm:** finest-level Correlation should reach a more negative value than −0.63, and
`maxDisp(in-body)` / the deformation grid should show larger, broader movement that follows the
target body contour.

> Open: if a single fine grid is too slow OR still under-fits, the proper fix is **per-level
> grid refinement** (coarse grid at coarse levels, fine at fine levels). That requires
> switching the optimizer to **LBFGS2** (`SetOptimizerAsLBFGS2`) + `SetInitialTransformAsBSpline`
> with `scaleFactors` — the combination L-BFGS-B could not do (Entry 3). This is the
> recommended next escalation if Entry 7's single-grid bump is insufficient.

### Entry 8 — Body-anchored, spacing-driven B-spline grid + sampling heuristic fix (2026-06-03)
Root problem found in the grid construction: `BSplineTransformInitializer(fixedCT, meshSize, 3)`
laid the mesh over the **entire CT FOV**, which for H&N is mostly air. A node *count* (e.g.
`5×5×5`) therefore put almost all control points outside the patient → the body was
**undersampled** independent of any metric mask. (Deployed config was running `5×5×5`, ~100 mm
effective spacing.)

**Fixes applied (B-spline path only; demons untouched):**
1. Grid is now driven by a **physical control-point spacing (mm) over the body extent**, not a
   node count over the FOV. The body region is the supplied fixed mask if present, else
   **auto-detected** from the CT (`BuildAutoBodyMask`: threshold ~−350 HU → largest connected
   component). A grid-reference image is cropped to that bbox + `MaskMarginMm` (reuses the
   previously-unused `CropImageToMaskBounds`), and `meshSize[axis] = round(extent / spacing)`,
   clamped to `[MinBSplineMeshCells=3, MaxBSplineMeshCells=40]`. Bonus: a B-spline is identity
   outside its grid domain, so confining the grid to the body also limits out-of-body warp even
   with no mask. Falls back to the legacy `BSplineGridNodes` count if body detection fails.
   New config: `BSplineControlPointSpacing` (mm, default 20) + per-site `BSplineSpacing` on
   `SitePreset`; the advanced-panel field/VM property switched from grid-nodes to CP-spacing.
2. **Sampling heuristic fixed.** The old `samplingPct = Max(samplingPct, 0.5)` whenever a mask
   was present assumed "mask = small ROI." For a **body** mask that's false (≈18.7% of volume,
   millions of voxels), so it was forcing 50% sampling = ~5× cost for no benefit. Now the 0.5
   floor only applies when the mask is genuinely small (< 5% of volume, via new
   `MaskInBodyFraction`); a body mask keeps the configured 0.1. Logged either way.

**Observed effect:** with an H&N body mask the grid resolved to `mesh=31x21x12 cells`
(`body-region 465×309×175 mm @ 15 mm`) ≈ **36.7k params** — first-iteration stall ~60 s. After
dropping H&N preset spacing **15 → 20 mm** (→ ~16.8k params) and the sampling fix (0.5 → 0.1),
the per-iteration cost fell ~9×. Even so, **B-spline + L-BFGS-B remains far slower than Varian's
DIR** — that's an algorithm-class gap (dense-field demons vs. per-parameter optimisation + line
search), not a tuning problem. Don't expect to tune it to demons-class speed.

> Reminder confirmed this session: the **demons path already implements out-of-body exclusion**
> (intersection intensity-masking pre-registration + rigid-only-outside-body field blend) and
> already runs < 5 min (~3 min in the logs). Every demons run to date used **no masks**; the
> "demons can't be masked" premise is wrong — it just hadn't been tested with masks.

### Entry 9 — NEW SYMPTOMS: demons+mask still pulls bolus; deformation-grid looks AP-shifted
First masked **demons** run (2026-06-03) + continued B-spline runs. Two symptoms reported:
(a) the deformation **pulls the source into the target bolus** despite body masks (both
algorithms); (b) the **deformation-grid overlay looks systematically shifted** off the contour —
visible warp in the air *anterior* to the patient, ~none along the *posterior* edge. User asked
whether the rigid registration introduces a systematic error.

**Diagnosis (strong hypotheses — not execution-verified; assistant can't run the app):**

- **The grid overlay draws the rigid+deformable COMPOSITE field, not deformable-only.**
  `DirReviewViewModel.RenderGrid` warps the grid by `reviewData.DisplacementField`, which the
  service fills from `TransformToDisplacementFieldFilter(finalTransform)` where `finalTransform`
  is the composite. Whole-grid mean displacement is ~53–57 mm — **dominated by the rigid**. So
  "warp in the air" is the rigid translation/rotation shown everywhere (outside the body the
  guard sets the field to rigid-only), and the apparent global shift is the rigid component
  *in the picture*, **not** a registration error. The deformed **dose** uses the transform
  correctly in physical space, so the dose can be fine even when the overlay looks shifted.
- **Second, real overlay bug:** `RenderGrid` adds the field's **world-frame** `dx`/`dy` (mm)
  directly to pixel **row/column indices**, assuming 1 px = 1 mm and an **identity image
  direction**. For a non-identity direction (orientation-dependent), the warp is drawn mirrored
  along an axis — which can place the deformation on the wrong A/P side. Visualization only;
  does not affect the dose. Proper fix: convert world→index via `Directionᵀ` and divide by
  spacing before drawing, and ideally render the **deformable-only** field (store it separately,
  before composing the rigid) so the overlay shows true deformation localized to the body.
- **Bolus pull is a SEPARATE, genuine issue.** The mask is provably co-registered to the fixed
  CT — `RasterizeStructureMask` (`ProjectPolygonToPixelSpace` + `FillPolygonIntoSlice`) and
  `ExtractCTBuffers` use the **same** `img.Origin`/`XDirection`/`YDirection` and the **same**
  `ix + iy*nx + z*nx*ny` layout, and `BuildMaskImage` gets the **same** geometry args as the CT
  → mask and fixed CT cannot be spatially shifted in SimpleITK space. So the bolus pull is
  **in-body**: most likely the target body/External contour **includes or abuts the bolus**, or
  the demons out-of-body guard isn't catching it. Next: dump/inspect that the target body mask
  truly *excludes* the bolus on the slices where the pull appears; verify the SOURCE body mask
  excludes its bolus too (Entry 6 open item).

> Flag: **the deformation-grid overlay is not a trustworthy view of *deformation*** right now —
> it shows rigid+deformable and ignores image direction/spacing. Fix the visualization first,
> then re-judge whether bolus is *actually* being pulled (vs. just looking that way).

### Entry 10 — Visualization fixes IMPLEMENTED + a REAL demons bug found (double rigid outside body)
**Two visualization fixes applied** (do not affect the deformed dose, only the review overlay):
1. **Deformable-only grid overlay.** The service now also stores `DeformableDisplacementField`
   (`reviewData`), computed as `fullField − rigidField` (rigid rebuilt via new
   `BuildInvertedRigidAffine`). Subtracting the rigid removes the ~5 cm rigid shift that dominated
   the grid and (for a correct composition) zeroes the out-of-body region, so the grid bunches only
   where real deformation occurs. `DirReviewViewModel` uses this field for the grid, falling back to
   the composite if absent.
2. **Orientation/spacing-correct warp.** `RenderGrid` previously added the field's **world-frame**
   `dx/dy` straight onto pixel indices (1 px = 1 mm, identity direction assumed). New `WarpNode`
   projects the physical displacement onto the image axes (columns of the direction matrix) and
   divides by spacing → correct, non-mirrored overlay on any orientation. `DirReviewData` gained a
   `Direction` field; the VM stores `_spacing`/`_direction`.

**REAL BUG found while tracing the demons path (NOT just visualization):** the masked demons +
rigid path **double-applies the rigid transform outside the body.** `fullResField` is the demons
field estimated on the **rigidly pre-aligned** moving image, so it is deformable-only
(`fixed → preAligned`). The mask blend sets inside-body = `fullResField`, outside-body =
`rigidOnlyField` (= `rigidAffine(x) − x`). Then the tail **unconditionally** composes
`rigidAffine ∘ dispTransform`:
- inside body: `rigidAffine(x + fullResField)` → single rigid ✓
- outside body: `rigidAffine(x + (rigidAffine(x)−x))` = `rigidAffine(rigidAffine(x))` → **double rigid ✗**

The `if (rigidAffine != null)` tail (comment: "No mask was set, so the flatten-and-blend block was
skipped") was meant for the **no-mask** case only, but its guard doesn't exclude the mask case. The
B-spline path is correct (bakes rigid into the field, no outer composite). This double-rigid is a
strong candidate cause of the masked-demons "warp in the air / systematic shift / bolus pull"
symptoms (Entry 9), independent of the overlay issue.

**Proposed fix (NOT yet applied — needs user go-ahead + a test run; registration-output change):**
mirror the B-spline path — for the masked demons case, build the **full** composite field (rigid
baked into the inside-body deformable) and blend `rigidOnly` outside, then wrap as a
`DisplacementFieldTransform` and **skip** the outer `rigidAffine` composite. Equivalent guard:
only apply the outer composite when **no** mask blend ran.

> Diagnostic prediction once the overlay fix is in: with the deformable-only overlay, the
> **B-spline** out-of-body grid should be ~flat (clean), while **demons** may still show a uniform
> out-of-body shift until the double-rigid bug is fixed — which would corroborate this entry.

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
- **Grid resolution** is now expressed as **`BSplineControlPointSpacing` (mm) over the body**
  (Entry 8), not `BSplineGridNodes` over the FOV: too fine → slow + boundary instability; too
  coarse → under-fits. `BSplineGridNodes` is now only a fallback if body detection fails.
- **Is the deformation-grid overlay misleading us?** (Entry 9) It renders the rigid+deformable
  composite and ignores image direction/spacing. Before chasing the "AP shift" as a registration
  bug, fix the overlay to show the **deformable-only** field converted to index space; then
  re-judge. Likely the dose is more correct than the picture suggests.
- **Bolus still pulled in with masks (both algorithms).** Mask is provably co-registered to the
  fixed CT (Entry 9), so this is in-body: confirm the target *and* source body contours actually
  exclude the bolus on the affected slices; check whether the demons out-of-body guard is active
  (needs both masks non-empty — see the `LogMaskCoverage` lines).

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
