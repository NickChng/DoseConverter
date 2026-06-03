# Deformable Registration Assessment — Bolus / Out-of-Body Masking

**Scope:** Senior review of the DIR pipeline in
`Model/DeformableRegistrationService.cs` against the clinical business need:

> Ensure a high-quality registration *within the specified target contour* (usually
> the body) so that a bolus present in the target image — but excluded from the body
> contour — does not have source tissue incorrectly mapped onto it.

**Status:** Partially resolved. The known *bugs* are fixed. The residual artefact the
user still observes is, in my assessment, a **structural limitation of the
diffeomorphic-demons algorithm**, not a remaining defect in the masking code. This
document explains what is guaranteed, what is only heuristic, and what the realistic
options are so the user can avoid expensive trial-and-error testing.

---

## 1. What the current pipeline does (as built)

Order of operations in `RunDiffeomorphicDemonsRegistration(...)`:

1. **Rigid pre-alignment** (optional): the moving CT *and* the moving body mask are
   resampled onto the fixed grid with the same rigid transform. (Fixes the earlier
   "floating ghost mask boundary 3–5 cm off the skin.")
2. **Intensity normalisation:** histogram-match moving→fixed, rescale both to [0, 255].
3. **Pre-registration image masking — INTERSECTION (AND)** of fixed and (rigidly
   aligned) moving body masks. Voxels outside *either* body are zeroed in *both*
   images. Bolus that exists only in the fixed image becomes `0` (fixed) vs `0`
   (moving) → zero intensity difference → **zero demons driving force there.**
4. **Diffeomorphic demons** multi-resolution registration on the masked images.
5. **Compose rigid + demons** in the correct order
   (`AddTransform(dispTransform); AddTransform(rigidAffine)`).
6. **Post-field blend — UNION (OR)** of both masks: inside the body the full
   rigid+deformable field is used; outside the body, the **rigid-only** displacement is
   preserved (not zero) so the resampler still lands on correct anatomy and does not
   ghost.

This is a sound, defensible design and all four fixes from the prior sessions should be
**kept**.

---

## 2. What is genuinely guaranteed vs. heuristic

| Behaviour | Guarantee level | Notes |
|---|---|---|
| Bolus-only region produces no *direct* demons force | **Strong** | AND-masking makes it 0-vs-0. |
| Final displacement field is exactly identity/rigid outside the body | **Strong** | Enforced explicitly by the post-field blend. |
| Moving mask boundary coincides with rigidly aligned anatomy | **Strong** | Mask is rigidly resampled with the CT. |
| No deformation *leaks* toward the bolus near the body surface | **Weak / heuristic** | This is the unresolved part — see §3. |
| Diffeomorphic (invertible, no folding) field | **Moderate** | Provided by the demons variant, but only *inside* the masked region; the hard mask edge stresses this. |

---

## 3. The hard limitation (root cause of the residual artefact)

`DiffeomorphicDemonsRegistrationFilter` (Thirion/PDE demons, as exposed by SimpleITK)
**has no native mask / fixed-image-region support.** The *only* way we can constrain it
is by editing image intensity — i.e. zeroing outside the contour. Two unavoidable
consequences follow:

1. **A hard intensity edge is created at the body surface.** Going from real tissue HU
   to a flat `0` outside the mask is a very high gradient. Demons computes large update
   forces at exactly that boundary.

2. **Gaussian field regularization spreads those edge forces.** Demons smooths the
   displacement field every iteration (`SmoothDisplacementFieldOn` /
   `SmoothUpdateFieldOn`). That smoothing kernel **bleeds** the boundary displacement
   both inward and outward over several voxels. Zeroing the field *afterward* (post-hoc
   masking) cannot undo a force that already perturbed the solution *inside* the body
   during optimization.

This is almost certainly the "defined, head-shaped boundary where the lines suddenly
jag" and the residual "tissue pulled toward the bolus" the user is seeing. It is
inherent to *masking by intensity-zeroing in a regularized demons solver* and **cannot
be fully removed by further tuning of the current masking logic.**

---

## 4. Recommended remediations (in priority order)

### 4.1 Reduce the edge gradient instead of creating a wall (cheap, do first)
Replacing the hard `0` fill outside the body with something that does not create a
cliff substantially lowers the spurious edge force:

- **Fill outside-body with a tissue-like constant** (e.g. the mean in-body intensity, or
  a smooth roll-off / feathered mask) so the inside↔outside transition is gentle. Both
  images get the same fill, so the *difference* is still ~0, but the *gradient* no longer
  spikes. This is the single highest-value, lowest-effort change.
- **Dilate the body mask by a few mm** before masking the images, then keep the
  *post*-field blend on the true (undilated) body. This moves the artificial edge away
  from the skin so its regularization bleed lands in the discarded margin rather than on
  real skin/subcutaneous tissue.

### 4.2 Crop to the mask bounding box (already partially present)
`CropImageToMaskBounds(...)` exists but is not wired into the active path. Cropping the
registration domain to the union (or fixed) body bounding box + margin reduces the
amount of irrelevant air the solver sees and shrinks the runtime. It does **not** by
itself solve the edge-bleed problem (§3) but composes well with §4.1.

### 4.3 Switch to a registration method that supports a real metric mask
If the edge-bleed artefact remains clinically unacceptable after §4.1, the correct
long-term fix is to move off PDE-demons to an **ITKv4 registration that accepts a
`FixedImageMask` / `MovingImageMask`**, e.g. SimpleITK's
`ImageRegistrationMethod` with `SetMetricFixedMask(...)` /
`SetMetricMovingMask(...)` and a B-spline or displacement-field transform. There, voxels
outside the mask are *excluded from the metric and its gradient* — no artificial edge is
created at all, which addresses the root cause rather than the symptom. Cost: a
non-trivial rewrite of `RunDiffeomorphicDemonsRegistration`, re-tuning, and
re-validation.

### 4.4 Symptom containment if none of the above is adopted now
The current post-field blend already forces the field to rigid-only outside the body, so
the *exported* deformation is safe outside the contour. The remaining risk is the few-mm
band of tissue *just inside* the skin near the bolus. Documenting that band as a known
limitation (and recommending reviewers inspect the skin/subcutaneous region near any
bolus) is a reasonable interim clinical control.

---

## 5. Verifying without expensive visual testing

Mask coverage is now logged at the start of every registration
(`LogMaskCoverage` in `DeformableRegistrationService.cs`). The log records the in-body
voxel count and % coverage for both masks, and warns if a mask is empty or absent.
**Before** any visual review, confirm in the log that:

- both `fixed (target) body mask` and `moving (source) body mask` report a non-zero
  voxel count with a plausible % (a body on a H&N CT is typically a large fraction of
  the in-FOV volume), and
- there is **no** `EMPTY` / `UNCONSTRAINED` warning.

An empty mask (e.g. a structure-id mismatch or a structure with no contours on that
image) would silently disable masking entirely and is the cheapest possible explanation
to rule out first.

---

## 6. Bottom line

- **Keep** all four prior fixes; they are correct.
- The remaining artefact is a **known limitation of intensity-masked demons**, not a
  masking bug.
- **First action:** §4.1 (feathered / tissue-fill outside body + small mask dilation) —
  low effort, directly attacks the edge-bleed root cause.
- **If still unacceptable:** §4.3 (move to a metric-mask-capable registration method) is
  the only approach that *structurally* guarantees out-of-body anatomy cannot influence
  the optimization.
- Use the new mask-coverage log lines to gate every expensive test run.

---

## 7. Implemented: selectable registration algorithm (Demons + B-spline metric masks)

The §4.3 recommendation is now **implemented as a user-selectable algorithm** rather than
a wholesale replacement, so the well-behaved demons path is retained for the no-mask case.

**What was added**
- A new `RegistrationAlgorithm` config attribute (`Demons` | `BSpline`) in
  `Schemas/DoseConverterConfig.xsd`, exposed by regenerating
  `Configuration/DoseConverterConfig.cs` with `xsd.exe` (default `Demons`).
- `RunBSplineMaskedRegistration(...)` in `Model/DeformableRegistrationService.cs`: a
  multi-resolution `ImageRegistrationMethod` using **Mattes mutual information**, a
  B-spline transform initialised over the fixed image, and **true metric masks** via
  `SetMetricFixedMask` / `SetMetricMovingMask`. The moving mask is rigidly aligned into
  the fixed frame first (same convention as the demons path), and the result is composed
  with the rigid pre-alignment so all downstream resampling/export is unchanged.
- An **Algorithm** selector in the DIR tab (`Views/DeformableRegistrationView.xaml`),
  wired through `ViewModels/DeformableRegistrationViewModel.cs` and passed to
  `PerformDIRAndWritePlan(...)`.

**Why the B-spline path structurally solves the bolus problem**
With `SetMetricFixedMask`/`SetMetricMovingMask`, voxels outside the body masks are
*excluded from the similarity metric and its gradient entirely*. No artificial intensity
edge is created at the skin (unlike intensity-zeroing for demons), so a bolus present on
only one image contributes **zero** to the optimisation and cannot pull tissue. This
removes the regularization edge-bleed described in §3 at its source.

**Guidance for use**
| Scenario | Recommended algorithm |
|---|---|
| No masks; smooth whole-image CT-CT alignment | **Diffeomorphic Demons** |
| Body masks set; bolus or other out-of-body anatomy present | **B-spline + metric masks** |

**Parameters (B-spline path), all in `RegistrationParameters` / the DIR tab**
- `BSplineGridNodes` (e.g. `5 5 5`) — control-point density; more nodes = finer local
  deformation, higher runtime and folding risk.
- `MetricSamplingPercentage`, `MaxIterationsPerLevel`, `CostFunctionConvergenceFactor` —
  optimiser controls (L-BFGS-B).
- `ShrinkFactorsPerLevel`, `SmoothingSigmasPerLevel` — shared multi-resolution pyramid.

**Known limitations / tuning notes for the B-spline path**
- B-spline registration is generally **less locally flexible** than demons for large,
  highly localised motion; increase `BSplineGridNodes` if it under-fits.
- Runtime scales with sampling percentage and grid density; start at the defaults and
  raise only if accuracy is insufficient.
- Mattes MI is robust for CT-CT, so the histogram-match/rescale pre-processing used by the
  demons path is intentionally **not** applied here.
- Verify the mask-coverage log lines (both masks non-empty) before committing to a visual
  review — an empty mask silently disables the metric masking.

