using System;
using System.Numerics;
using System.Linq;
using ContextPin;

static class BoardRegistrationChecks
{
    public static void Run()
    {
        var board = LabBoards.Resolve(LabBoards.PhysicalId);
        var simulation = LabBoards.Resolve(GuideBoard.Id);
        Expect(board.IsValidVersion && simulation.IsValidVersion && board.contentHash != simulation.contentHash,
            "physical and simulated boards are independently versioned");
        Expect(board.PlacementComparison("A1", "C1") == "equivalent" && !board.SameNet("A1", "F1"),
            "A1 and C1 share the left strip, while the trench separates F1");
        Expect(board.PlacementComparison("A1", "A6") == "different_net",
            "different numbered physical strips have different electrical nodes");
        Expect(simulation.PlacementComparison("A1", "C1") == "different_net",
            "legacy simulation preserves its visibly independent A and C rails");
        Expect(board.IsExpectedConnection("resistor", "+1", "C1") &&
            board.IsExpectedConnection("led", "C1", "A6") &&
            !board.IsExpectedConnection("led", "A6", "C1") &&
            !board.IsExpectedConnection("resistor", "A1", "C1") &&
            !board.IsExpectedConnection("ground", "A6", "+1"),
            "physical wiring matches the designated netlist and distinguishes polarity and same-net contact");

        var modified = board.Copy();
        modified.terminals[0].netId = "unapproved";
        Expect(!modified.IsValidVersion && LabBoards.Resolve(LabBoards.PhysicalId).IsValidVersion,
            "editing a working copy cannot change the known board revision");

        var teaching = Samples(board, new Vector3(.12f, .84f, 1.15f), .45f);
        Expect(ManualBoardRegistration.TryFit(board, teaching.Take(3).ToArray(), out var preview, out _) &&
            !ManualBoardRegistration.Confirm(preview, "teaching", "demonstration", "real", 1, 1, DateTime.UtcNow),
            "the three contacts enable a preview and confirmation still needs independent checks");

        Expect(ManualBoardRegistration.TryFit(board, teaching, out var demonstration, out _) &&
            ManualBoardRegistration.Confirm(demonstration, "teaching", "demonstration", "real", 1, 2, DateTime.UtcNow) &&
            demonstration.precision == "REGION" && demonstration.cornerErrorMeters < .0001f &&
            demonstration.middleErrorMeters < .0001f,
            "both independent points enable the explicit region-level confirmation");

        var studentPose = Samples(board, new Vector3(-.2f, .89f, .55f), -1.12f);
        Expect(ManualBoardRegistration.TryFit(board, studentPose, out var practice, out _) &&
            ManualBoardRegistration.Confirm(practice, "student", "practice", "simulation", 2, 3, DateTime.UtcNow),
            "student registration independently confirms the same board version at a different pose");
        var terminal = board.FindTerminal("C1").boardLocal;
        var teachingPoint = demonstration.ToWorld(terminal);
        var practicePoint = practice.ToWorld(terminal);
        Expect(Vector3.Distance(teachingPoint.Vector, practicePoint.Vector) > .3f &&
            Vector3.Distance(practice.ToBoard(practicePoint).Vector, terminal.Vector) < .0001f &&
            practice.boardInstanceId != demonstration.boardInstanceId && practice.source == "simulation" &&
            practice.Matches(board) && !practice.Matches(simulation) && !practice.Matches(modified),
            "a logical terminal is reprojected through the learner's own pose without relabeling provenance");
        Expect(practice.IsCurrent("student", 2, 3) && !practice.IsCurrent("student", 2, 4) &&
            !practice.IsCurrent("student", 1, 3) && !practice.IsCurrent("teaching", 2, 3),
            "a capture from another board, registration revision or tracking epoch cannot update the active pose");

        var incorrect = teaching.ToArray();
        incorrect[4] = new BoardVector(incorrect[4].Vector + new Vector3(0, .02f, 0));
        Expect(!ManualBoardRegistration.TryFit(board, incorrect, out _, out _),
            "the independently sampled midpoint rejects an otherwise valid fit");
        incorrect = teaching.ToArray();
        incorrect[2] = incorrect[1];
        Expect(!ManualBoardRegistration.TryFit(board, incorrect, out _, out _),
            "reused or collinear reference contact fails registration");

        var reflected = board.references.Select(reference =>
            new BoardVector(reference.boardLocal.x, 1, -reference.boardLocal.z)).ToArray();
        Expect(!ManualBoardRegistration.TryFit(board, reflected, out _, out _),
            "mirrored registration contradicts the upward normal");
        Expect(!ManualBoardRegistration.TryFit(simulation, teaching, out _, out _),
            "synthetic panel coordinates are ineligible as a physical registration profile");
    }

    static BoardVector[] Samples(LabBoardDefinition board, Vector3 translation, float rotation)
    {
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotation);
        return board.references.Select(item =>
            new BoardVector(translation + Vector3.Transform(item.boardLocal.Vector, yaw))).ToArray();
    }

    static void Expect(bool pass, string explanation)
    {
        if (!pass) throw new InvalidOperationException(explanation);
        Console.WriteLine("PASS " + explanation);
    }
}
