using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using LatticeGenerator;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
[assembly: AssemblyInformationalVersion("1.3")]
[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {
        private const string TemporaryColdEnvelopeId = "zzLrtCold";
        private const string TemporaryHotReferenceId = "zzLrtRef";
        private const string TemporaryHotTargetId = "zzLrtHotT";
        private const string TemporaryHotAllowedId = "zzLrtHotA";
        private const double GuidelineMinimumHotRatioPercent = 2.0;
        private const double GuidelineMaximumHotRatioPercent = 4.0;
        private const int ColdOverlapSampleCount = 513;
        private const int EclipseStructureLimit = 99;

        private static readonly Color HotColor = Color.FromRgb(218, 45, 205);
        private static readonly Color ColdColor = Color.FromRgb(35, 137, 230);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context)
        {
            StructureSet structureSet = context.StructureSet;
            if (structureSet == null)
            {
                MessageBox.Show(
                    "Open a patient with a structure set before running the LATTICE Generator.",
                    "LATTICE Generator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var targets = structureSet.Structures
                .Where(structure =>
                    !structure.IsEmpty &&
                    string.Equals(structure.DicomType, "GTV", StringComparison.OrdinalIgnoreCase))
                .Select(ToOption)
                .ToList();

            if (targets.Count == 0)
            {
                MessageBox.Show(
                    "The current structure set contains no non-empty structure with DICOM type GTV.",
                    "No target available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var selectableStructures = structureSet.Structures
                .Where(structure =>
                    !structure.IsEmpty &&
                    !string.Equals(structure.DicomType, "EXTERNAL", StringComparison.OrdinalIgnoreCase) &&
                    !IsOwnedOutputId(structure.Id) &&
                    !IsTemporaryId(structure.Id))
                .Select(ToOption)
                .ToList();

            var dialog = new LatticeDialog(targets, selectableStructures);
            if (dialog.ShowDialog() != true || dialog.Selection == null)
            {
                return;
            }

            Structure selectedTarget = FindStructure(structureSet, dialog.Selection.TargetId);
            var selectedProtectionStructures = dialog.Selection.AvoidanceStructureIds
                .Select(id => FindStructure(structureSet, id))
                .Where(structure => structure != null)
                .ToList();

            GenerateLatticeGeometry(
                context,
                selectedTarget,
                selectedProtectionStructures,
                dialog.Selection.Parameters);
        }

        private static LatticeStructureOption ToOption(Structure structure)
        {
            return new LatticeStructureOption
            {
                Id = structure.Id,
                VolumeCc = structure.Volume
            };
        }

        private static Structure FindStructure(StructureSet structureSet, string id)
        {
            return structureSet.Structures.FirstOrDefault(structure =>
                string.Equals(structure.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private void GenerateLatticeGeometry(
            ScriptContext context,
            Structure target,
            IReadOnlyList<Structure> protectionStructures,
            LatticeParameters parameters)
        {
            LatticeValidationResult validation = LatticeGeometryMath.Validate(parameters);
            if (target == null || !validation.IsValid)
            {
                string details = target == null
                    ? "The selected target is no longer available."
                    : string.Join(
                        Environment.NewLine,
                        validation.Errors.Select(error => error.Value));
                MessageBox.Show(
                    details,
                    "Invalid LATTICE input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            double radiusMm = parameters.DiameterCm * 5.0;
            double spacingMm = parameters.SeparationCm * 10.0;
            double hotBorderClearanceMm = parameters.HotBorderClearanceCm * 10.0;
            double coldEnvelopeExpansionMm = parameters.ColdEnvelopeExpansionCm * 10.0;
            double protectionExpansionMm =
                radiusMm + parameters.ProtectionClearanceCm * 10.0;
            double minimumColdTargetOverlapFraction =
                parameters.MinimumColdTargetOverlapPercent / 100.0;
            int hotDoseLabelGy = (int)Math.Round(parameters.HotDoseLabelGy);
            IReadOnlyList<LatticePoint> coldOverlapSampleOffsets =
                LatticeGeometryMath.CreateSymmetricSphereVolumeSampleOffsets(
                    radiusMm,
                    ColdOverlapSampleCount);

            StructureSet structureSet = context.StructureSet;
            int persistentStructureCount = structureSet.Structures.Count(
                structure => !IsTemporaryId(structure.Id));
            if (persistentStructureCount > EclipseStructureLimit - 2)
            {
                MessageBox.Show(
                    "Eclipse permits at most 99 structures. Only " +
                    Math.Max(0, EclipseStructureLimit - persistentStructureCount) +
                    " slot(s) are currently available, but the protected analysis and " +
                    "combined fallback require two temporary structures. No existing " +
                    "final LATTICE output was replaced.",
                    "Insufficient structure slots",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            bool modificationsStarted = false;
            bool completed = false;
            try
            {
                context.Patient.BeginModifications();
                modificationsStarted = true;
                RemoveTemporaryStructures(structureSet);

                Structure coldEnvelope = structureSet.AddStructure(
                    "CONTROL",
                    TemporaryColdEnvelopeId);
                coldEnvelope.SegmentVolume = target.SegmentVolume.Margin(
                    coldEnvelopeExpansionMm);

                Structure hotRegion = structureSet.AddStructure(
                    "CONTROL",
                    TemporaryHotTargetId);
                hotRegion.SegmentVolume = target.SegmentVolume.Margin(
                    -hotBorderClearanceMm);

                if (hotRegion.IsEmpty)
                {
                    ShowNoGeometryMessage(
                        "The target is too small for the configured hot-spot border clearance.");
                    return;
                }
                double hotReferenceVolumeCc = hotRegion.Volume;

                hotRegion.SegmentVolume = hotRegion.SegmentVolume.Margin(-radiusMm);

                if (hotRegion.IsEmpty)
                {
                    ShowNoGeometryMessage(
                        "The target is too small for the hot-spot radius plus border clearance.");
                    return;
                }

                LatticePlacementOption selectedPlacement = FindPreferredPlacement(
                    coldEnvelope,
                    hotReferenceVolumeCc,
                    hotRegion,
                    protectionStructures,
                    protectionExpansionMm,
                    spacingMm,
                    radiusMm,
                    parameters.TiltDegrees,
                    target,
                    coldOverlapSampleOffsets,
                    minimumColdTargetOverlapFraction);

                if (selectedPlacement == null)
                {
                    ShowNoGeometryMessage(
                        "No alternating hot/cold lattice fits the configured geometry.");
                    return;
                }

                int occupiedPlaneCount = selectedPlacement.Placement.HotPoints
                    .Concat(selectedPlacement.Placement.ColdPoints)
                    .Select(point => point.K)
                    .Distinct()
                    .Count();
                int requiredLayerStructureCount =
                    LatticeGeometryMath.CountRequiredLayerOutputStructures(
                        selectedPlacement.Placement);
                LatticeOutputPlan outputPlan =
                    LatticeGeometryMath.PlanOutputStructures(
                        parameters.CreateLayerStructures,
                        requiredLayerStructureCount,
                        persistentStructureCount,
                        EclipseStructureLimit);
                if (!outputPlan.CanCreateOutput)
                {
                    MessageBox.Show(
                        "Eclipse permits at most 99 structures. Only " +
                        outputPlan.AvailableStructureSlots +
                        " slot(s) are currently available, but even combined output " +
                        "requires two temporary structures so existing results can remain " +
                        "untouched until replacement is complete.",
                        "Insufficient structure slots",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                MessageBoxResult confirmation = MessageBox.Show(
                    BuildPlacementSummary(
                        target,
                        protectionStructures.Count,
                        parameters,
                        selectedPlacement,
                        occupiedPlaneCount,
                        hotReferenceVolumeCc,
                        radiusMm,
                        outputPlan,
                        includeConfirmationQuestion: true),
                    "Confirm automatic LATTICE placement",
                    MessageBoxButton.YesNo,
                    IsInsideGuideline(selectedPlacement.HotRatioPercent) &&
                    protectionStructures.Count > 0 &&
                    !outputPlan.FellBackToCombined
                        ? MessageBoxImage.Question
                        : MessageBoxImage.Warning);

                if (confirmation != MessageBoxResult.Yes)
                {
                    return;
                }

                RemoveTemporaryGeometryStructures(structureSet);
                List<PendingOutput> pendingOutputs = CreatePendingOutputs(
                    structureSet,
                    selectedPlacement.Placement,
                    outputPlan.UseLayerStructures,
                    hotDoseLabelGy,
                    radiusMm);

                ReplaceFinalStructures(structureSet, pendingOutputs);
                completed = true;

                MessageBox.Show(
                    BuildPlacementSummary(
                        target,
                        protectionStructures.Count,
                        parameters,
                        selectedPlacement,
                        occupiedPlaneCount,
                        hotReferenceVolumeCc,
                        radiusMm,
                        outputPlan,
                        includeConfirmationQuestion: false),
                    "LATTICE hot/cold structures generated",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "LATTICE structures were not created." +
                    Environment.NewLine +
                    Environment.NewLine +
                    exception.GetType().Name + ": " + exception.Message,
                    "LATTICE generation failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (modificationsStarted && !completed)
                {
                    TryRemoveTemporaryStructures(structureSet);
                }
            }
        }

        private static LatticePlacementOption FindPreferredPlacement(
            Structure coldEnvelope,
            double hotReferenceVolumeCc,
            Structure hotRegion,
            IReadOnlyList<Structure> protectionStructures,
            double protectionExpansionMm,
            double spacingMm,
            double radiusMm,
            double tiltDegrees,
            Structure target,
            IReadOnlyList<LatticePoint> coldOverlapSampleOffsets,
            double minimumColdTargetOverlapFraction)
        {
            VVector center = coldEnvelope.CenterPoint;
            var bounds = coldEnvelope.MeshGeometry.Bounds;
            double maximumXDistance = Math.Max(
                Math.Abs(center.x - bounds.X),
                Math.Abs(center.x - (bounds.X + bounds.SizeX)));
            double maximumYDistance = Math.Max(
                Math.Abs(center.y - bounds.Y),
                Math.Abs(center.y - (bounds.Y + bounds.SizeY)));
            double maximumZDistance = Math.Max(
                Math.Abs(center.z - bounds.Z),
                Math.Abs(center.z - (bounds.Z + bounds.SizeZ)));
            double halfDiagonalMm = Math.Sqrt(
                maximumXDistance * maximumXDistance +
                maximumYDistance * maximumYDistance +
                maximumZDistance * maximumZDistance);

            IReadOnlyList<LatticePoint> phases =
                LatticeGeometryMath.CreateHalfSpacingPhases(spacingMm);
            var options = new List<LatticePlacementOption>();
            var pointsInsideHotTarget = new HashSet<LatticePoint>();

            for (int phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
            {
                IReadOnlyList<LatticeGridPoint> candidates =
                    LatticeGeometryMath.CreateRotatedGridCandidates(
                        new LatticePoint(center.x, center.y, center.z),
                        halfDiagonalMm,
                        spacingMm,
                        tiltDegrees,
                        phases[phaseIndex]);
                foreach (LatticeGridPoint candidate in candidates)
                {
                    if (hotRegion.IsPointInsideSegment(ToVVector(candidate.Position)))
                    {
                        pointsInsideHotTarget.Add(candidate.Position);
                    }
                }
            }

            foreach (Structure protection in protectionStructures)
            {
                hotRegion.SegmentVolume = hotRegion.SegmentVolume.Sub(
                    protection.SegmentVolume.Margin(protectionExpansionMm));
            }
            if (hotRegion.IsEmpty)
            {
                return null;
            }

            for (int phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
            {
                IReadOnlyList<LatticeGridPoint> candidates =
                    LatticeGeometryMath.CreateRotatedGridCandidates(
                        new LatticePoint(center.x, center.y, center.z),
                        halfDiagonalMm,
                        spacingMm,
                        tiltDegrees,
                        phases[phaseIndex]);
                for (int hotParity = 0; hotParity <= 1; hotParity++)
                {
                    LatticePlacementResult placement =
                        LatticeGeometryMath.ClassifyGridPoints(
                            candidates,
                            hotParity,
                            point => coldEnvelope.IsPointInsideSegment(ToVVector(point)),
                            point => pointsInsideHotTarget.Contains(point),
                            point => hotRegion.IsPointInsideSegment(ToVVector(point)),
                            point => HasMinimumTargetOverlap(
                                target,
                                point,
                                coldOverlapSampleOffsets,
                                minimumColdTargetOverlapFraction));
                    options.Add(new LatticePlacementOption
                    {
                        PhaseIndex = phaseIndex,
                        HotParity = hotParity,
                        Placement = placement,
                        HotRatioPercent =
                            LatticeGeometryMath.CalculateSpotVolumeRatioPercent(
                                placement.HotPoints.Count,
                                hotReferenceVolumeCc,
                                radiusMm)
                    });
                }
            }

            return LatticeGeometryMath.ChoosePreferredPlacement(
                options,
                GuidelineMinimumHotRatioPercent,
                GuidelineMaximumHotRatioPercent);
        }

        private static bool HasMinimumTargetOverlap(
            Structure target,
            LatticePoint center,
            IReadOnlyList<LatticePoint> sampleOffsets,
            double minimumFraction)
        {
            if (minimumFraction <= 0.0)
            {
                return true;
            }

            int requiredInsideCount = (int)Math.Ceiling(
                minimumFraction * sampleOffsets.Count - 0.0000001);
            int insideCount = 0;

            for (int index = 0; index < sampleOffsets.Count; index++)
            {
                LatticePoint offset = sampleOffsets[index];
                var samplePoint = new VVector(
                    center.X + offset.X,
                    center.Y + offset.Y,
                    center.Z + offset.Z);
                if (target.IsPointInsideSegment(samplePoint))
                {
                    insideCount++;
                }

                if (insideCount >= requiredInsideCount)
                {
                    return true;
                }

                int remainingCount = sampleOffsets.Count - index - 1;
                if (insideCount + remainingCount < requiredInsideCount)
                {
                    return false;
                }
            }

            return insideCount >= requiredInsideCount;
        }

        private static List<PendingOutput> CreatePendingOutputs(
            StructureSet structureSet,
            LatticePlacementResult placement,
            bool createLayerStructures,
            int hotDoseLabelGy,
            double radiusMm)
        {
            var outputs = new List<PendingOutput>();
            if (!createLayerStructures)
            {
                Structure hot = AddPendingStructure(
                    structureSet,
                    "zzLrtHAll",
                    GetCombinedHotId(hotDoseLabelGy),
                    HotColor,
                    outputs);
                foreach (LatticeGridPoint point in placement.HotPoints)
                {
                    DrawSphere(hot, ToVVector(point.Position), radiusMm, structureSet.Image);
                }

                Structure cold = AddPendingStructure(
                    structureSet,
                    "zzLrtCAll",
                    "PTV_ColdSpots",
                    ColdColor,
                    outputs);
                foreach (LatticeGridPoint point in placement.ColdPoints)
                {
                    DrawSphere(cold, ToVVector(point.Position), radiusMm, structureSet.Image);
                }

                return outputs;
            }

            int[] layers = placement.HotPoints
                .Concat(placement.ColdPoints)
                .Select(point => point.K)
                .Distinct()
                .OrderBy(k => k)
                .ToArray();

            for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                int layerNumber = layerIndex + 1;
                int k = layers[layerIndex];
                LatticeGridPoint[] hotPoints = placement.HotPoints
                    .Where(point => point.K == k)
                    .ToArray();
                LatticeGridPoint[] coldPoints = placement.ColdPoints
                    .Where(point => point.K == k)
                    .ToArray();

                if (hotPoints.Length > 0)
                {
                    Structure hot = AddPendingStructure(
                        structureSet,
                        "zzLrtH" + layerNumber.ToString("D2"),
                        GetLayerHotId(layerNumber, hotDoseLabelGy),
                        HotColor,
                        outputs);
                    foreach (LatticeGridPoint point in hotPoints)
                    {
                        DrawSphere(
                            hot,
                            ToVVector(point.Position),
                            radiusMm,
                            structureSet.Image);
                    }
                }

                if (coldPoints.Length > 0)
                {
                    Structure cold = AddPendingStructure(
                        structureSet,
                        "zzLrtC" + layerNumber.ToString("D2"),
                        "PTV_ColdSpot_" + layerNumber,
                        ColdColor,
                        outputs);
                    foreach (LatticeGridPoint point in coldPoints)
                    {
                        DrawSphere(
                            cold,
                            ToVVector(point.Position),
                            radiusMm,
                            structureSet.Image);
                    }
                }
            }

            return outputs;
        }

        private static Structure AddPendingStructure(
            StructureSet structureSet,
            string temporaryId,
            string finalId,
            Color color,
            ICollection<PendingOutput> outputs)
        {
            if (finalId.Length > 16)
            {
                throw new InvalidOperationException(
                    "Generated structure ID exceeds 16 characters: " + finalId);
            }

            if (!structureSet.CanAddStructure("PTV", temporaryId))
            {
                throw new InvalidOperationException(
                    "Eclipse cannot add temporary PTV structure " + temporaryId + ".");
            }

            Structure structure = structureSet.AddStructure("PTV", temporaryId);
            structure.Color = color;
            outputs.Add(new PendingOutput
            {
                Structure = structure,
                FinalId = finalId
            });
            return structure;
        }

        private static void ReplaceFinalStructures(
            StructureSet structureSet,
            IReadOnlyList<PendingOutput> pendingOutputs)
        {
            List<Structure> existingOutputs = structureSet.Structures
                .Where(item => IsOwnedOutputId(item.Id))
                .ToList();
            Structure nonRemovable = existingOutputs.FirstOrDefault(
                structure => !structureSet.CanRemoveStructure(structure));
            if (nonRemovable != null)
            {
                throw new InvalidOperationException(
                    "Existing output structure " + nonRemovable.Id +
                    " is in use and cannot be replaced.");
            }

            foreach (Structure structure in existingOutputs)
            {
                structureSet.RemoveStructure(structure);
            }

            foreach (PendingOutput output in pendingOutputs)
            {
                output.Structure.Id = output.FinalId;
            }
        }

        private static string BuildPlacementSummary(
            Structure target,
            int protectionStructureCount,
            LatticeParameters parameters,
            LatticePlacementOption selectedPlacement,
            int occupiedPlaneCount,
            double hotReferenceVolumeCc,
            double radiusMm,
            LatticeOutputPlan outputPlan,
            bool includeConfirmationQuestion)
        {
            LatticePlacementResult placement = selectedPlacement.Placement;
            var message = new StringBuilder();
            message.AppendLine(
                includeConfirmationQuestion
                    ? "Automatic placement analysis"
                    : "LATTICE hot/cold structures were generated.");
            message.AppendLine();
            message.AppendLine("Target: " + target.Id);
            message.AppendLine("Hot spots: " + placement.HotPoints.Count);
            message.AppendLine("Cold spots: " + placement.ColdPoints.Count);
            message.AppendLine("Occupied lattice planes: " + occupiedPlaneCount);
            message.AppendLine(
                "Hot candidates omitted at target border: " +
                placement.OmittedHotAtTargetBorder);
            message.AppendLine(
                "Hot candidates omitted for PRV/OAR protection: " +
                placement.OmittedHotForProtection);
            message.AppendLine(
                "Cold candidates omitted below " +
                parameters.MinimumColdTargetOverlapPercent.ToString("F0") +
                "% GTV overlap: " +
                placement.OmittedColdForInsufficientTargetOverlap);
            message.AppendLine(
                "Selected protection structures: " + protectionStructureCount);
            message.AppendLine(
                "Selected grid phase: " + (selectedPlacement.PhaseIndex + 1) +
                " of 8, hot parity " + selectedPlacement.HotParity);
            message.AppendLine(
                "Spot diameter / grid spacing: " +
                parameters.DiameterCm.ToString("F1") + " / " +
                parameters.SeparationCm.ToString("F1") + " cm");
            message.AppendLine(
                "Hot reference volume: " + hotReferenceVolumeCc.ToString("F1") + " cc");
            message.AppendLine(
                "Analytical hot-volume ratio: " +
                selectedPlacement.HotRatioPercent.ToString("F2") +
                "% (reference: 2-4%)");
            message.AppendLine(
                "Analytical volume per spot: " +
                LatticeGeometryMath.CalculateSphereVolumeCc(radiusMm).ToString("F2") +
                " cc");
            message.AppendLine(
                "Output: " +
                (outputPlan.UseLayerStructures
                    ? "hot and cold spots grouped per occupied grid plane (never per spot)"
                    : "one combined hot and one combined cold structure"));
            message.AppendLine();
            message.AppendLine(
                "Cold spots are retained near selected protection structures. " +
                "Only unsafe hot positions are omitted.");
            message.AppendLine(
                "No generated point was removed to force the reported volume ratio.");

            if (outputPlan.FellBackToCombined)
            {
                message.AppendLine();
                message.AppendLine(
                    "AUTOMATIC OUTPUT SWITCH: Plane output requires " +
                    outputPlan.RequiredLayerStructureCount +
                    " new structures, but only " +
                    outputPlan.AvailableStructureSlots +
                    " slot(s) are available below the Eclipse limit of 99 while " +
                    "existing outputs remain protected. The script " +
                    (includeConfirmationQuestion ? "will create" : "created") +
                    " one combined hot and one combined cold structure instead.");
            }

            if (!IsInsideGuideline(selectedPlacement.HotRatioPercent))
            {
                message.AppendLine();
                message.AppendLine(
                    "WARNING: The analytical hot-volume ratio is outside the 2-4% " +
                    "reference range. Review diameter, spacing, and target geometry.");
            }

            if (protectionStructureCount == 0)
            {
                message.AppendLine();
                message.AppendLine(
                    "WARNING: No PRV/OAR protection structure is selected. " +
                    "Hot spots were checked only against the target border.");
            }

            if (includeConfirmationQuestion)
            {
                message.AppendLine();
                message.Append("Create these structures?");
            }
            else
            {
                message.AppendLine();
                message.Append("Review every generated contour before planning or clinical use.");
            }

            return message.ToString();
        }

        private static bool IsInsideGuideline(double ratioPercent)
        {
            return ratioPercent >= GuidelineMinimumHotRatioPercent &&
                ratioPercent <= GuidelineMaximumHotRatioPercent;
        }

        private static void ShowNoGeometryMessage(string reason)
        {
            MessageBox.Show(
                reason +
                Environment.NewLine +
                Environment.NewLine +
                "No existing final LATTICE output was replaced.",
                "No valid LATTICE geometry",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static string GetLayerHotId(int layerNumber, int hotDoseLabelGy)
        {
            return "PTV_high_" + layerNumber + "_" + hotDoseLabelGy + "Gy";
        }

        private static string GetCombinedHotId(int hotDoseLabelGy)
        {
            return "PTV_High_" + hotDoseLabelGy + "Gy";
        }

        private static void RemoveTemporaryGeometryStructures(StructureSet structureSet)
        {
            foreach (string id in new[]
            {
                TemporaryColdEnvelopeId,
                TemporaryHotReferenceId,
                TemporaryHotTargetId,
                TemporaryHotAllowedId
            })
            {
                RemoveStructureIfPresent(structureSet, id);
            }
        }

        private static void RemoveTemporaryStructures(StructureSet structureSet)
        {
            foreach (Structure structure in structureSet.Structures
                .Where(item => IsTemporaryId(item.Id))
                .ToList())
            {
                structureSet.RemoveStructure(structure);
            }
        }

        private static void TryRemoveTemporaryStructures(StructureSet structureSet)
        {
            try
            {
                RemoveTemporaryStructures(structureSet);
            }
            catch
            {
                // Preserve the original generation error; temporary IDs remain recognizable.
            }
        }

        private static void RemoveStructureIfPresent(StructureSet structureSet, string id)
        {
            Structure structure = FindStructure(structureSet, id);
            if (structure != null)
            {
                structureSet.RemoveStructure(structure);
            }
        }

        private static bool IsOwnedOutputId(string id)
        {
            string value = id ?? string.Empty;
            return
                string.Equals(value, "PTV_ColdSpots", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(
                    value,
                    @"^PTV_High_\d+Gy$",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(
                    value,
                    @"^PTV_high_\d+_\d+Gy$",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(
                    value,
                    @"^PTV_ColdSpot_\d+$",
                    RegexOptions.IgnoreCase) ||
                string.Equals(value, "LRT_Volume", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "LRT_Vertices", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(value, @"^zV_\d+$", RegexOptions.IgnoreCase);
        }

        private static bool IsTemporaryId(string id)
        {
            string value = id ?? string.Empty;
            return
                string.Equals(
                    value,
                    TemporaryColdEnvelopeId,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    TemporaryHotReferenceId,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    TemporaryHotTargetId,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    TemporaryHotAllowedId,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "zzLrtHAll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "zzLrtCAll", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(value, @"^zzLrt[HC]\d+$", RegexOptions.IgnoreCase);
        }

        private static VVector ToVVector(LatticePoint point)
        {
            return new VVector(point.X, point.Y, point.Z);
        }

        private static void DrawSphere(
            Structure structure,
            VVector center,
            double radiusMm,
            VMS.TPS.Common.Model.API.Image image)
        {
            double zResolutionMm = image.ZRes;
            int minimumSlice = Math.Max(
                0,
                (int)Math.Floor((center.z - radiusMm - image.Origin.z) / zResolutionMm));
            int maximumSlice = Math.Min(
                image.ZSize - 1,
                (int)Math.Ceiling((center.z + radiusMm - image.Origin.z) / zResolutionMm));

            for (int slice = minimumSlice; slice <= maximumSlice; slice++)
            {
                double z = image.Origin.z + slice * zResolutionMm;
                double sliceRadiusMm = Math.Sqrt(
                    Math.Max(0.0, radiusMm * radiusMm - Math.Pow(z - center.z, 2.0)));

                if (sliceRadiusMm > 0.5)
                {
                    structure.AddContourOnImagePlane(
                        GenerateCircle(
                            new VVector(center.x, center.y, z),
                            sliceRadiusMm),
                        slice);
                }
            }
        }

        private static VVector[] GenerateCircle(VVector center, double radiusMm, int segments = 48)
        {
            var points = new VVector[segments];
            for (int index = 0; index < segments; index++)
            {
                double angle = index * 2.0 * Math.PI / segments;
                points[index] = new VVector(
                    center.x + radiusMm * Math.Cos(angle),
                    center.y + radiusMm * Math.Sin(angle),
                    center.z);
            }

            return points;
        }

        private sealed class PendingOutput
        {
            public Structure Structure { get; set; }
            public string FinalId { get; set; }
        }
    }
}
