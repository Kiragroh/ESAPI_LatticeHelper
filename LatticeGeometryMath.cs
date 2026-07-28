using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LatticeGenerator
{
    public sealed class LatticeParameters
    {
        public double DiameterCm { get; set; }
        public double SeparationCm { get; set; }
        public double TiltDegrees { get; set; }
        public double HotBorderClearanceCm { get; set; }
        public double ColdEnvelopeExpansionCm { get; set; }
        public double ProtectionClearanceCm { get; set; }
        public double MinimumColdTargetOverlapPercent { get; set; }
        public double HotDoseLabelGy { get; set; }
        public bool CreateLayerStructures { get; set; }
    }

    public sealed class LatticeValidationResult
    {
        public LatticeValidationResult(IDictionary<string, string> errors)
        {
            Errors = new Dictionary<string, string>(errors);
        }

        public IReadOnlyDictionary<string, string> Errors { get; private set; }

        public bool IsValid
        {
            get { return Errors.Count == 0; }
        }
    }

    public struct LatticePoint
    {
        public LatticePoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
    }

    public struct LatticeGridPoint
    {
        public LatticeGridPoint(LatticePoint position, int i, int j, int k)
        {
            Position = position;
            I = i;
            J = j;
            K = k;
        }

        public LatticePoint Position { get; private set; }
        public int I { get; private set; }
        public int J { get; private set; }
        public int K { get; private set; }

        public int Parity
        {
            get { return (I + J + K) & 1; }
        }
    }

    public sealed class LatticePlacementResult
    {
        public LatticePlacementResult(
            IEnumerable<LatticeGridPoint> hotPoints,
            IEnumerable<LatticeGridPoint> coldPoints,
            int omittedHotAtTargetBorder,
            int omittedHotForProtection,
            int omittedColdForInsufficientTargetOverlap)
        {
            HotPoints = hotPoints.ToList();
            ColdPoints = coldPoints.ToList();
            OmittedHotAtTargetBorder = omittedHotAtTargetBorder;
            OmittedHotForProtection = omittedHotForProtection;
            OmittedColdForInsufficientTargetOverlap =
                omittedColdForInsufficientTargetOverlap;
        }

        public IReadOnlyList<LatticeGridPoint> HotPoints { get; private set; }
        public IReadOnlyList<LatticeGridPoint> ColdPoints { get; private set; }
        public int OmittedHotAtTargetBorder { get; private set; }
        public int OmittedHotForProtection { get; private set; }
        public int OmittedColdForInsufficientTargetOverlap { get; private set; }

        public int TotalPlacedCount
        {
            get { return HotPoints.Count + ColdPoints.Count; }
        }
    }

    public sealed class LatticePlacementOption
    {
        public int PhaseIndex { get; set; }
        public int HotParity { get; set; }
        public double HotRatioPercent { get; set; }
        public LatticePlacementResult Placement { get; set; }
    }

    public sealed class LatticeOutputPlan
    {
        public bool UseLayerStructures { get; set; }
        public bool FellBackToCombined { get; set; }
        public bool CanCreateOutput { get; set; }
        public int RequiredLayerStructureCount { get; set; }
        public int AvailableStructureSlots { get; set; }
    }

    public static class LatticeGeometryMath
    {
        private const double ComparisonTolerance = 0.0000001;

        public static LatticeValidationResult Validate(LatticeParameters parameters)
        {
            var errors = new Dictionary<string, string>();
            if (parameters == null)
            {
                errors["Parameters"] = "No parameters were supplied.";
                return new LatticeValidationResult(errors);
            }

            ValidatePositive(
                parameters.DiameterCm,
                nameof(LatticeParameters.DiameterCm),
                "Spot diameter must be greater than 0 cm.",
                errors);
            ValidatePositive(
                parameters.SeparationCm,
                nameof(LatticeParameters.SeparationCm),
                "Grid spacing must be greater than 0 cm.",
                errors);

            if (IsFinite(parameters.DiameterCm) &&
                IsFinite(parameters.SeparationCm) &&
                parameters.DiameterCm > 0.0 &&
                parameters.SeparationCm > 0.0 &&
                parameters.SeparationCm + ComparisonTolerance < parameters.DiameterCm)
            {
                errors[nameof(LatticeParameters.SeparationCm)] =
                    "Grid spacing must be at least the spot diameter.";
            }

            if (!IsFinite(parameters.TiltDegrees) ||
                parameters.TiltDegrees < -45.0 ||
                parameters.TiltDegrees > 45.0)
            {
                errors[nameof(LatticeParameters.TiltDegrees)] =
                    "Grid tilt must be between -45 and 45 degrees.";
            }

            ValidateNonNegative(
                parameters.HotBorderClearanceCm,
                nameof(LatticeParameters.HotBorderClearanceCm),
                "Hot-spot target-border clearance cannot be negative.",
                errors);
            ValidateNonNegative(
                parameters.ColdEnvelopeExpansionCm,
                nameof(LatticeParameters.ColdEnvelopeExpansionCm),
                "Cold-spot envelope expansion cannot be negative.",
                errors);
            ValidateNonNegative(
                parameters.ProtectionClearanceCm,
                nameof(LatticeParameters.ProtectionClearanceCm),
                "Additional protection clearance cannot be negative.",
                errors);

            if (!IsFinite(parameters.MinimumColdTargetOverlapPercent) ||
                parameters.MinimumColdTargetOverlapPercent < 0.0 ||
                parameters.MinimumColdTargetOverlapPercent > 100.0)
            {
                errors[nameof(LatticeParameters.MinimumColdTargetOverlapPercent)] =
                    "Minimum cold-spot GTV overlap must be between 0 and 100 percent.";
            }

            if (!IsFinite(parameters.HotDoseLabelGy) ||
                parameters.HotDoseLabelGy < 1.0 ||
                parameters.HotDoseLabelGy > 99.0 ||
                Math.Abs(parameters.HotDoseLabelGy - Math.Round(parameters.HotDoseLabelGy)) >
                    ComparisonTolerance)
            {
                errors[nameof(LatticeParameters.HotDoseLabelGy)] =
                    "The hot-spot dose label must be a whole number from 1 to 99 Gy.";
            }

            return new LatticeValidationResult(errors);
        }

        public static bool TryParseFlexibleDouble(string text, out double value)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out value))
            {
                return true;
            }

            if (double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value))
            {
                return true;
            }

            return double.TryParse(
                trimmed.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        public static double CalculateSphereVolumeCc(double radiusMm)
        {
            double radiusCm = radiusMm / 10.0;
            return 4.0 / 3.0 * Math.PI * radiusCm * radiusCm * radiusCm;
        }

        public static double CalculateSpotVolumeRatioPercent(
            int spotCount,
            double referenceVolumeCc,
            double radiusMm)
        {
            if (spotCount <= 0 ||
                !IsFinite(referenceVolumeCc) ||
                referenceVolumeCc <= 0.0 ||
                !IsFinite(radiusMm) ||
                radiusMm <= 0.0)
            {
                return 0.0;
            }

            return spotCount * CalculateSphereVolumeCc(radiusMm) /
                referenceVolumeCc * 100.0;
        }

        public static IReadOnlyList<LatticePoint> CreateHalfSpacingPhases(double spacingMm)
        {
            double half = spacingMm / 2.0;
            var phases = new List<LatticePoint>(8);
            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        phases.Add(new LatticePoint(x * half, y * half, z * half));
                    }
                }
            }

            return phases;
        }

        public static IReadOnlyList<LatticePoint> CreateSymmetricSphereVolumeSampleOffsets(
            double radiusMm,
            int sampleCount)
        {
            if (!IsFinite(radiusMm) || radiusMm <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(radiusMm));
            }

            if (sampleCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleCount));
            }

            var samples = new List<LatticePoint>(sampleCount)
            {
                new LatticePoint(0.0, 0.0, 0.0)
            };

            int index = 1;
            while (samples.Count < sampleCount)
            {
                double x = 2.0 * Halton(index, 2) - 1.0;
                double y = 2.0 * Halton(index, 3) - 1.0;
                double z = 2.0 * Halton(index, 5) - 1.0;
                index++;

                double squaredLength = x * x + y * y + z * z;
                if (squaredLength > 1.0 || squaredLength < ComparisonTolerance)
                {
                    continue;
                }

                var sample = new LatticePoint(
                    x * radiusMm,
                    y * radiusMm,
                    z * radiusMm);
                samples.Add(sample);
                if (samples.Count < sampleCount)
                {
                    samples.Add(new LatticePoint(
                        -sample.X,
                        -sample.Y,
                        -sample.Z));
                }
            }

            return samples;
        }

        public static IReadOnlyList<LatticeGridPoint> CreateRotatedGridCandidates(
            LatticePoint center,
            double halfDiagonalMm,
            double spacingMm,
            double tiltDegrees,
            LatticePoint phase)
        {
            if (halfDiagonalMm < 0.0 || spacingMm <= 0.0)
            {
                return new List<LatticeGridPoint>();
            }

            double tiltRadians = tiltDegrees * Math.PI / 180.0;
            double cosTilt = Math.Cos(tiltRadians);
            double sinTilt = Math.Sin(tiltRadians);
            int extent = (int)Math.Ceiling((halfDiagonalMm + spacingMm) / spacingMm);
            if (extent > 40)
            {
                throw new InvalidOperationException(
                    "The requested grid would be too large. Increase the grid spacing.");
            }

            var points = new List<LatticeGridPoint>();
            for (int i = -extent; i <= extent; i++)
            {
                double localX = i * spacingMm + phase.X;
                for (int j = -extent; j <= extent; j++)
                {
                    double localY = j * spacingMm + phase.Y;
                    for (int k = -extent; k <= extent; k++)
                    {
                        double localZ = k * spacingMm + phase.Z;
                        double rotatedY = localY * cosTilt - localZ * sinTilt;
                        double rotatedZ = localY * sinTilt + localZ * cosTilt;
                        points.Add(new LatticeGridPoint(
                            new LatticePoint(
                                center.X + localX,
                                center.Y + rotatedY,
                                center.Z + rotatedZ),
                            i,
                            j,
                            k));
                    }
                }
            }

            return points;
        }

        public static LatticePlacementResult ClassifyGridPoints(
            IEnumerable<LatticeGridPoint> candidatePoints,
            int hotParity,
            Func<LatticePoint, bool> isInsideColdEnvelope,
            Func<LatticePoint, bool> isInsideHotTarget,
            Func<LatticePoint, bool> isInsideHotAllowedRegion,
            Func<LatticePoint, bool> hasSufficientColdTargetOverlap)
        {
            if (candidatePoints == null)
            {
                throw new ArgumentNullException(nameof(candidatePoints));
            }

            var hot = new List<LatticeGridPoint>();
            var cold = new List<LatticeGridPoint>();
            int omittedAtBorder = 0;
            int omittedForProtection = 0;
            int omittedColdForOverlap = 0;

            foreach (LatticeGridPoint point in candidatePoints)
            {
                if (!isInsideColdEnvelope(point.Position))
                {
                    continue;
                }

                if (point.Parity != hotParity)
                {
                    if (hasSufficientColdTargetOverlap(point.Position))
                    {
                        cold.Add(point);
                    }
                    else
                    {
                        omittedColdForOverlap++;
                    }
                    continue;
                }

                if (!isInsideHotTarget(point.Position))
                {
                    omittedAtBorder++;
                }
                else if (!isInsideHotAllowedRegion(point.Position))
                {
                    omittedForProtection++;
                }
                else
                {
                    hot.Add(point);
                }
            }

            return new LatticePlacementResult(
                hot,
                cold,
                omittedAtBorder,
                omittedForProtection,
                omittedColdForOverlap);
        }

        public static LatticePlacementOption ChoosePreferredPlacement(
            IEnumerable<LatticePlacementOption> options,
            double guidelineMinimumPercent,
            double guidelineMaximumPercent)
        {
            return options
                .Where(option =>
                    option != null &&
                    option.Placement != null &&
                    option.Placement.HotPoints.Count > 0 &&
                    option.Placement.ColdPoints.Count > 0)
                .OrderBy(option => RatioBandDistance(
                    option.HotRatioPercent,
                    guidelineMinimumPercent,
                    guidelineMaximumPercent))
                .ThenByDescending(option => option.Placement.TotalPlacedCount)
                .ThenBy(option => Math.Abs(
                    option.HotRatioPercent - guidelineMinimumPercent))
                .ThenBy(option => option.PhaseIndex)
                .ThenBy(option => option.HotParity)
                .FirstOrDefault();
        }

        public static int CountRequiredLayerOutputStructures(
            LatticePlacementResult placement)
        {
            if (placement == null)
            {
                return 0;
            }

            return placement.HotPoints.Select(point => point.K).Distinct().Count() +
                placement.ColdPoints.Select(point => point.K).Distinct().Count();
        }

        public static LatticeOutputPlan PlanOutputStructures(
            bool layerOutputRequested,
            int requiredLayerStructureCount,
            int currentStructureCount,
            int maximumStructureCount)
        {
            int availableSlots = Math.Max(0, maximumStructureCount - currentStructureCount);
            bool layerOutputFits =
                layerOutputRequested && requiredLayerStructureCount <= availableSlots;
            bool combinedOutputFits = availableSlots >= 2;

            return new LatticeOutputPlan
            {
                UseLayerStructures = layerOutputFits,
                FellBackToCombined =
                    layerOutputRequested && !layerOutputFits && combinedOutputFits,
                CanCreateOutput = layerOutputFits || combinedOutputFits,
                RequiredLayerStructureCount = requiredLayerStructureCount,
                AvailableStructureSlots = availableSlots
            };
        }

        private static double RatioBandDistance(
            double ratioPercent,
            double minimumPercent,
            double maximumPercent)
        {
            if (ratioPercent < minimumPercent)
            {
                return minimumPercent - ratioPercent;
            }

            if (ratioPercent > maximumPercent)
            {
                return ratioPercent - maximumPercent;
            }

            return 0.0;
        }

        private static double Halton(int index, int numberBase)
        {
            double result = 0.0;
            double fraction = 1.0 / numberBase;
            int value = index;
            while (value > 0)
            {
                result += fraction * (value % numberBase);
                value /= numberBase;
                fraction /= numberBase;
            }

            return result;
        }

        private static void ValidatePositive(
            double value,
            string field,
            string message,
            IDictionary<string, string> errors)
        {
            if (!IsFinite(value) || value <= 0.0)
            {
                errors[field] = message;
            }
        }

        private static void ValidateNonNegative(
            double value,
            string field,
            string message,
            IDictionary<string, string> errors)
        {
            if (!IsFinite(value) || value < 0.0)
            {
                errors[field] = message;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
