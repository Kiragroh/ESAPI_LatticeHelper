using System;
using System.Collections.Generic;
using System.Linq;
using LatticeGenerator;

internal static class LatticeGeometryMathTests
{
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Run("Reference-style defaults are valid", ReferenceDefaultsAreValid);
        Run("Decimal dots and commas are accepted", DecimalDotsAndCommasAreAccepted);
        Run("Spacing must fit the spot diameter", SpacingMustFitDiameter);
        Run("Clearances and dose label are validated", ClearancesAndDoseLabelAreValidated);
        Run("Thirty-one 1.5 cm hot spots are approximately two percent", ReferenceRatioIsApproximatelyTwoPercent);
        Run("Eight unique half-spacing phases are generated", EightGridPhasesAreGenerated);
        Run("Three-dimensional checkerboard creates equal hot and cold sets", CheckerboardCreatesHotAndColdSets);
        Run("Protection regions remove hot spots but retain cold spots", ProtectionOnlyRemovesHotSpots);
        Run("Target-border clearance removes unsafe hot spots", TargetBorderRemovesUnsafeHotSpots);
        Run("Cold spots below fifty percent GTV overlap are omitted", ColdOverlapThresholdIsApplied);
        Run("Sphere sampling is deterministic and bounded", SphereSamplingIsDeterministicAndBounded);
        Run("Layer output counts structures per occupied grid plane", LayerOutputCountsStructuresPerPlane);
        Run("Layer output falls back to combined at the structure limit", LayerOutputFallsBackAtStructureLimit);
        Run("Preferred placement keeps all points and favors the reference ratio", PreferredPlacementUsesReferenceRatioWithoutTrimming);
        Run("Grid indices survive rotated candidate generation", GridIndicesSurviveGeneration);
        Run("Pathological grid sizes are rejected", PathologicalGridSizesAreRejected);

        Console.WriteLine();
        Console.WriteLine(_failed == 0
            ? "All LatticeGeometryMath tests passed."
            : _failed + " LatticeGeometryMath test(s) failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void ReferenceDefaultsAreValid()
    {
        LatticeValidationResult result = LatticeGeometryMath.Validate(CreateDefaults());
        AssertTrue(
            result.IsValid,
            string.Join("; ", result.Errors.Select(error => error.Key + ": " + error.Value)));
    }

    private static void DecimalDotsAndCommasAreAccepted()
    {
        double dotValue;
        double commaValue;
        AssertTrue(
            LatticeGeometryMath.TryParseFlexibleDouble("1.25", out dotValue),
            "Decimal dot was rejected.");
        AssertTrue(
            LatticeGeometryMath.TryParseFlexibleDouble("1,25", out commaValue),
            "Decimal comma was rejected.");
        AssertNear(1.25, dotValue, 0.000001);
        AssertNear(1.25, commaValue, 0.000001);
    }

    private static void SpacingMustFitDiameter()
    {
        LatticeParameters parameters = CreateDefaults();
        parameters.DiameterCm = 3.1;
        parameters.SeparationCm = 3.0;

        AssertHasError(
            LatticeGeometryMath.Validate(parameters),
            nameof(LatticeParameters.SeparationCm));
    }

    private static void ClearancesAndDoseLabelAreValidated()
    {
        LatticeParameters parameters = CreateDefaults();
        parameters.HotBorderClearanceCm = -0.1;
        parameters.ColdEnvelopeExpansionCm = -0.1;
        parameters.ProtectionClearanceCm = -0.1;
        parameters.MinimumColdTargetOverlapPercent = 101.0;
        parameters.HotDoseLabelGy = 50.5;

        LatticeValidationResult result = LatticeGeometryMath.Validate(parameters);
        AssertHasError(result, nameof(LatticeParameters.HotBorderClearanceCm));
        AssertHasError(result, nameof(LatticeParameters.ColdEnvelopeExpansionCm));
        AssertHasError(result, nameof(LatticeParameters.ProtectionClearanceCm));
        AssertHasError(result, nameof(LatticeParameters.MinimumColdTargetOverlapPercent));
        AssertHasError(result, nameof(LatticeParameters.HotDoseLabelGy));
    }

    private static void ReferenceRatioIsApproximatelyTwoPercent()
    {
        double ratio = LatticeGeometryMath.CalculateSpotVolumeRatioPercent(
            spotCount: 31,
            referenceVolumeCc: 2810.9,
            radiusMm: 7.5);

        AssertNear(1.95, ratio, 0.02);
    }

    private static void EightGridPhasesAreGenerated()
    {
        IReadOnlyList<LatticePoint> phases = LatticeGeometryMath.CreateHalfSpacingPhases(30.0);

        AssertEqual(8, phases.Count);
        AssertEqual(8, phases.Select(FormatPoint).Distinct().Count());
        AssertTrue(
            phases.Any(point => point.X == 0.0 && point.Y == 0.0 && point.Z == 0.0),
            "Missing origin phase.");
        AssertTrue(
            phases.Any(point => point.X == 15.0 && point.Y == 15.0 && point.Z == 15.0),
            "Missing half-spacing phase.");
    }

    private static void CheckerboardCreatesHotAndColdSets()
    {
        var points = new List<LatticeGridPoint>();
        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                for (int k = 0; k <= 1; k++)
                {
                    points.Add(new LatticeGridPoint(
                        new LatticePoint(i, j, k),
                        i,
                        j,
                        k));
                }
            }
        }

        LatticePlacementResult result = LatticeGeometryMath.ClassifyGridPoints(
            points,
            hotParity: 0,
            isInsideColdEnvelope: point => true,
            isInsideHotTarget: point => true,
            isInsideHotAllowedRegion: point => true,
            hasSufficientColdTargetOverlap: point => true);

        AssertEqual(4, result.HotPoints.Count);
        AssertEqual(4, result.ColdPoints.Count);
        AssertEqual(8, result.TotalPlacedCount);
    }

    private static void ProtectionOnlyRemovesHotSpots()
    {
        var protectedHot = new LatticeGridPoint(
            new LatticePoint(0.0, 0.0, 0.0),
            0,
            0,
            0);
        var safeHot = new LatticeGridPoint(
            new LatticePoint(10.0, 10.0, 0.0),
            1,
            1,
            0);
        var coldInsideProtection = new LatticeGridPoint(
            new LatticePoint(0.0, 10.0, 0.0),
            0,
            1,
            0);

        LatticePlacementResult result = LatticeGeometryMath.ClassifyGridPoints(
            new[] { protectedHot, safeHot, coldInsideProtection },
            hotParity: 0,
            isInsideColdEnvelope: point => true,
            isInsideHotTarget: point => true,
            isInsideHotAllowedRegion: point => point.X > 0.0,
            hasSufficientColdTargetOverlap: point => true);

        AssertEqual(1, result.HotPoints.Count);
        AssertEqual(1, result.ColdPoints.Count);
        AssertEqual(1, result.OmittedHotForProtection);
        AssertTrue(
            result.ColdPoints.Any(point => point.Position.X == 0.0),
            "Cold spot inside the protection area was incorrectly removed.");
    }

    private static void TargetBorderRemovesUnsafeHotSpots()
    {
        var borderHot = new LatticeGridPoint(
            new LatticePoint(0.0, 0.0, 0.0),
            0,
            0,
            0);
        var safeHot = new LatticeGridPoint(
            new LatticePoint(20.0, 0.0, 0.0),
            2,
            0,
            0);

        LatticePlacementResult result = LatticeGeometryMath.ClassifyGridPoints(
            new[] { borderHot, safeHot },
            hotParity: 0,
            isInsideColdEnvelope: point => true,
            isInsideHotTarget: point => point.X > 0.0,
            isInsideHotAllowedRegion: point => true,
            hasSufficientColdTargetOverlap: point => true);

        AssertEqual(1, result.HotPoints.Count);
        AssertEqual(1, result.OmittedHotAtTargetBorder);
    }

    private static void ColdOverlapThresholdIsApplied()
    {
        var insufficientCold = new LatticeGridPoint(
            new LatticePoint(49.0, 0.0, 0.0),
            1,
            0,
            0);
        var acceptedCold = new LatticeGridPoint(
            new LatticePoint(50.0, 0.0, 0.0),
            1,
            0,
            0);

        LatticePlacementResult result = LatticeGeometryMath.ClassifyGridPoints(
            new[] { insufficientCold, acceptedCold },
            hotParity: 0,
            isInsideColdEnvelope: point => true,
            isInsideHotTarget: point => true,
            isInsideHotAllowedRegion: point => true,
            hasSufficientColdTargetOverlap: point => point.X >= 50.0);

        AssertEqual(1, result.ColdPoints.Count);
        AssertEqual(1, result.OmittedColdForInsufficientTargetOverlap);
        AssertNear(50.0, result.ColdPoints[0].Position.X, 0.000001);
    }

    private static void SphereSamplingIsDeterministicAndBounded()
    {
        IReadOnlyList<LatticePoint> first =
            LatticeGeometryMath.CreateSymmetricSphereVolumeSampleOffsets(7.5, 513);
        IReadOnlyList<LatticePoint> second =
            LatticeGeometryMath.CreateSymmetricSphereVolumeSampleOffsets(7.5, 513);

        AssertEqual(513, first.Count);
        AssertEqual(
            string.Join(";", first.Select(FormatPoint)),
            string.Join(";", second.Select(FormatPoint)));
        AssertTrue(
            first.All(point =>
                point.X * point.X + point.Y * point.Y + point.Z * point.Z <=
                7.5 * 7.5 + 0.000001),
            "A sample offset was outside the sphere.");
    }

    private static void LayerOutputCountsStructuresPerPlane()
    {
        var hot = new[]
        {
            new LatticeGridPoint(new LatticePoint(0.0, 0.0, 0.0), 0, 0, 0),
            new LatticeGridPoint(new LatticePoint(0.0, 0.0, 10.0), 0, 0, 1)
        };
        var cold = new[]
        {
            new LatticeGridPoint(new LatticePoint(1.0, 0.0, 0.0), 1, 0, 0),
            new LatticeGridPoint(new LatticePoint(1.0, 0.0, 10.0), 1, 0, 1),
            new LatticeGridPoint(new LatticePoint(1.0, 0.0, 20.0), 1, 0, 2)
        };
        var placement = new LatticePlacementResult(hot, cold, 0, 0, 0);

        AssertEqual(
            5,
            LatticeGeometryMath.CountRequiredLayerOutputStructures(placement));
    }

    private static void LayerOutputFallsBackAtStructureLimit()
    {
        LatticeOutputPlan fallback = LatticeGeometryMath.PlanOutputStructures(
            layerOutputRequested: true,
            requiredLayerStructureCount: 14,
            currentStructureCount: 90,
            maximumStructureCount: 99);
        AssertTrue(fallback.CanCreateOutput, "Combined output should still fit.");
        AssertTrue(fallback.FellBackToCombined, "Expected automatic combined fallback.");
        AssertTrue(!fallback.UseLayerStructures, "Layer output should be disabled.");
        AssertEqual(9, fallback.AvailableStructureSlots);

        LatticeOutputPlan exactCombined = LatticeGeometryMath.PlanOutputStructures(
            layerOutputRequested: true,
            requiredLayerStructureCount: 14,
            currentStructureCount: 97,
            maximumStructureCount: 99);
        AssertTrue(exactCombined.CanCreateOutput, "Combined output should fit exactly.");
        AssertTrue(
            exactCombined.FellBackToCombined,
            "Expected combined fallback with exactly two available slots.");
        AssertEqual(2, exactCombined.AvailableStructureSlots);

        LatticeOutputPlan layer = LatticeGeometryMath.PlanOutputStructures(
            layerOutputRequested: true,
            requiredLayerStructureCount: 14,
            currentStructureCount: 40,
            maximumStructureCount: 99);
        AssertTrue(layer.CanCreateOutput, "Layer output should fit.");
        AssertTrue(layer.UseLayerStructures, "Layer output was unexpectedly disabled.");
        AssertTrue(!layer.FellBackToCombined, "Unexpected combined fallback.");

        LatticeOutputPlan blocked = LatticeGeometryMath.PlanOutputStructures(
            layerOutputRequested: true,
            requiredLayerStructureCount: 2,
            currentStructureCount: 98,
            maximumStructureCount: 99);
        AssertTrue(!blocked.CanCreateOutput, "Even two combined structures should not fit.");
    }

    private static void PreferredPlacementUsesReferenceRatioWithoutTrimming()
    {
        var lowerRatioPlacement = new LatticePlacementOption
        {
            PhaseIndex = 2,
            HotParity = 0,
            HotRatioPercent = 2.1,
            Placement = CreatePlacement(hotCount: 31, coldCount: 62)
        };
        var upperRatioPlacement = new LatticePlacementOption
        {
            PhaseIndex = 2,
            HotParity = 1,
            HotRatioPercent = 3.9,
            Placement = CreatePlacement(hotCount: 62, coldCount: 31)
        };

        LatticePlacementOption selected = LatticeGeometryMath.ChoosePreferredPlacement(
            new[] { upperRatioPlacement, lowerRatioPlacement },
            guidelineMinimumPercent: 2.0,
            guidelineMaximumPercent: 4.0);

        AssertEqual(31, selected.Placement.HotPoints.Count);
        AssertEqual(62, selected.Placement.ColdPoints.Count);
        AssertEqual(93, selected.Placement.TotalPlacedCount);
    }

    private static void GridIndicesSurviveGeneration()
    {
        IReadOnlyList<LatticeGridPoint> points = LatticeGeometryMath.CreateRotatedGridCandidates(
            new LatticePoint(0.0, 0.0, 0.0),
            halfDiagonalMm: 0.0,
            spacingMm: 30.0,
            tiltDegrees: 0.0,
            phase: new LatticePoint(0.0, 0.0, 0.0));

        LatticeGridPoint origin = points.Single(point =>
            point.I == 0 && point.J == 0 && point.K == 0);
        AssertNear(0.0, origin.Position.X, 0.000001);
        AssertNear(0.0, origin.Position.Y, 0.000001);
        AssertNear(0.0, origin.Position.Z, 0.000001);
        AssertEqual(0, origin.Parity);
    }

    private static void PathologicalGridSizesAreRejected()
    {
        bool threw = false;
        try
        {
            LatticeGeometryMath.CreateRotatedGridCandidates(
                new LatticePoint(0.0, 0.0, 0.0),
                halfDiagonalMm: 1000.0,
                spacingMm: 1.0,
                tiltDegrees: 0.0,
                phase: new LatticePoint(0.0, 0.0, 0.0));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        AssertTrue(threw, "Expected an oversized grid to be rejected before allocation.");
    }

    private static LatticePlacementResult CreatePlacement(int hotCount, int coldCount)
    {
        var hot = Enumerable.Range(0, hotCount)
            .Select(index => new LatticeGridPoint(
                new LatticePoint(index, 0.0, 0.0),
                index * 2,
                0,
                0))
            .ToList();
        var cold = Enumerable.Range(0, coldCount)
            .Select(index => new LatticeGridPoint(
                new LatticePoint(index, 1.0, 0.0),
                index * 2 + 1,
                0,
                0))
            .ToList();
        return new LatticePlacementResult(hot, cold, 0, 0, 0);
    }

    private static LatticeParameters CreateDefaults()
    {
        return new LatticeParameters
        {
            DiameterCm = 1.5,
            SeparationCm = 3.0,
            TiltDegrees = 0.0,
            HotBorderClearanceCm = 0.6,
            ColdEnvelopeExpansionCm = 0.5,
            ProtectionClearanceCm = 0.0,
            MinimumColdTargetOverlapPercent = 50.0,
            HotDoseLabelGy = 50.0,
            CreateLayerStructures = true
        };
    }

    private static string FormatPoint(LatticePoint point)
    {
        return point.X + "|" + point.Y + "|" + point.Z;
    }

    private static void AssertHasError(LatticeValidationResult result, string field)
    {
        AssertTrue(
            result.Errors.ContainsKey(field),
            "Expected validation error for " + field + ".");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                "Expected " + expected + " but got " + actual + ".");
        }
    }

    private static void AssertNear(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                "Expected " + expected + " but got " + actual + ".");
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }
}
