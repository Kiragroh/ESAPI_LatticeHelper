# Changelog

## 1.3 - 2026-07-28

- Published the project as `ESAPI_LatticeHelper`.
- Added Eclipse screenshots and DOI-linked LATTICE literature references.
- Excluded the institutional presentation and development DICOM reference dataset
  from the public repository.
- Added a configurable minimum cold-spot volume inside the selected GTV, with a
  50% default.
- Added deterministic symmetric 3D sphere sampling with 513 points for the
  cold-spot overlap estimate.
- Added the number of cold candidates omitted below the overlap threshold to the
  confirmation and completion summaries.
- Clarified that detailed output is grouped per occupied grid plane and never
  creates one structure per individual sphere.
- Added a pre-write check against Eclipse's 99-structure limit.
- Added a visible automatic fallback from plane output to exactly one combined
  hot and one combined cold structure when the plane structures do not fit.
- Added a guarded abort that preserves existing final outputs when even two
  combined replacement structures cannot be prepared.
- Updated assembly identity to version 1.3.

## 1.2 - 2026-07-28

- Replaced the single vertex set with an indexed 3D checkerboard that automatically
  creates separate hot and cold spot sets.
- Matched the included reference geometry defaults: 1.5 cm spot diameter, 3.0 cm
  spacing, 0.6 cm hot-border clearance, and 0.5 cm cold-envelope expansion.
- Applied target-border and selected PRV/OAR protection only to hot candidates;
  alternating cold spots remain near protection regions.
- Removed volume-ratio truncation. The hot ratio is calculated, reported, and used
  only to choose among complete grid phases/parity assignments.
- Added a pre-generation summary with hot/cold counts, occupied planes, border
  omissions, protection omissions, phase/parity, and hot-volume ratio.
- Added reference-style plane structures (`PTV_high_*_*Gy` and
  `PTV_ColdSpot_*`) plus optional combined output.
- Assigned magenta to hot outputs and blue to cold outputs.
- Replaced dose fall-off controls with direct geometry, cold-envelope, protection,
  and naming parameters.

## 1.1 - 2026-07-28

- Replaced the fixed-size mixed-language dialog with a resizable, scrollable,
  English WPF interface.
- Added explicit light styles for all controls, including a host-independent
  checkbox template and readable disabled fields.
- Added field-level validation with decimal-dot and decimal-comma parsing.
- Removed the hidden 50 cc GTV filter.
- Added a configurable 4% default volume-ratio limit with a 10% maximum.
- Added eight grid phases and spatially balanced point reduction.
- Corrected OAR clearance to include the full sphere radius.
- Kept existing final outputs until replacement geometry is complete and limited
  cleanup to owned exact structure IDs.
- Expanded the user and implementation documentation.
