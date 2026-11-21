using ContextPin;
using System.Text.Json;
int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
Experience Captured() => ExperienceRules.DemoCapture("A", DemoBoards.Create("A").FindRole("confirm"), "important", "evidence.png");
var card=Captured();
Check(!ExperienceRules.Transfer(card,DemoBoards.Create("B")).allowed,"unreviewed instruction cannot transfer");
Check(ExperienceRules.Approve(card)=="","evidence-backed card can be approved");
var decision=ExperienceRules.Transfer(card,DemoBoards.Create("B"));
Check(decision.allowed && decision.target.id=="B-3","transfer uses role across changed IDs and positions");
Check(!ExperienceRules.Transfer(card,DemoBoards.Create("C")).allowed,"missing target blocks transfer");
var b=DemoBoards.Create("B");b.yellowOn=false;
Check(!ExperienceRules.Transfer(card,b).allowed,"unsatisfied current condition blocks transfer");
b=DemoBoards.Create("B");b.targets.Add(b.FindRole("confirm"));
Check(!ExperienceRules.Transfer(card,b).allowed,"ambiguous role mapping blocks transfer");
b=DemoBoards.Create("B");b.task="unrelated";
Check(!ExperienceRules.Transfer(card,b).allowed,"different task cannot inherit instructions");
var question=ExperienceRules.DemoCapture("A", DemoBoards.Create("A").FindRole("confirm"), "question", "");
Check(ExperienceRules.Approve(question)!="","question tag never invents an answer");
var tampered=Captured();tampered.sourceQuote="unspoken instruction";
Check(ExperienceRules.Approve(tampered)!="","missing evidence is rejected");
tampered=Captured();tampered.actionKey="hold_for_10_seconds";
Check(ExperienceRules.Approve(tampered)!="","unsupported generated action is rejected");
Check(card.outcomes.Count==0,"expected outcome is not a measured outcome");
b=DemoBoards.Create("B");Check(!b.Press("cancel")&&!b.blueOn,"wrong action does not report success");
Check(b.Press("confirm")&&b.blueOn,"correct action causes observed board state change");
Check(!ExperienceRules.Transfer(null,null).allowed,"missing data fails closed");

var guide = GuideRules.DemoDraft();
var sourceIds = guide.steps.Select(step => step.sourceNoteId).ToArray();
var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
Check(GuideRules.NewRun(guide, false, start) == null && GuideRules.Approve(guide, sourceIds).Length > 0,
    "the example cannot start before the ambiguous resistor contact is resolved");
guide.steps[1].toPoint = "a-1";
Check(GuideRules.Approve(guide, sourceIds.Skip(1)).Length > 0,
    "a row decision cannot replace evidence from the recorded session");
guide.steps[2].ledDirection = "";
Check(GuideRules.Approve(guide, sourceIds).Length > 0, "missing LED orientation blocks approval");
guide.steps[2].ledDirection = GuideRules.LedDirection;
guide.steps[2].fromPoint = "a-1";
Check(GuideRules.Approve(guide, sourceIds).Length > 0, "an approved diagram cannot reuse an occupied hole");
guide.steps[2].fromPoint = "a-2";
Check(GuideRules.Approve(guide, sourceIds).Length == 0, "complete low-voltage circuit can be published");

var run = GuideRules.NewRun(guide, false, start);
Check(run != null && !GuideRules.SelectPoint(run, "a-1") && GuideRules.Confirm(run, start),
    "student acknowledges power off and materials before virtual wiring");
guide.steps[1].toPoint = "c-1";
Check(GuideRules.Current(run).toPoint == "a-1", "a student's run retains its approved guide snapshot");
guide.steps[1].toPoint = "a-1";

bool SubmitPair(GuideRun attempt, string from, string to, int seconds)
{
    GuideRules.ClearSelection(attempt);
    GuideRules.SelectPoint(attempt, from);
    GuideRules.SelectPoint(attempt, to);
    return GuideRules.Submit(attempt, start.AddSeconds(seconds));
}

Check(!SubmitPair(run, "plus-1", "c-1", 5) && run.stepIndex == 1 && run.wrongAttempts == 1 &&
    run.feedback.Contains("A행"), "wrong electrical row shows the required destination at the same step");
GuideRules.Hint(run);
var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
run = JsonSerializer.Deserialize<GuideRun>(JsonSerializer.Serialize(run, jsonOptions), jsonOptions);
Check(run.hintUses == 1 && run.toPoint == "c-1" && run.stepIndex == 1,
    "run serialization retains selections, hints and the pending correction");
Check(SubmitPair(run, "a-2", "plus-2", 9) && run.correctionSeconds == 4 &&
    GuideRules.AvailablePoint(run, "a-2") == "a-1",
    "a resistor accepts either direction and an alternate hole on the same node");
Check(!SubmitPair(run, "a-2", "b-1", 12) && run.feedback.Contains("이미 사용"),
    "an occupied hole suggests the free hole in its electrical row");
Check(!SubmitPair(run, "b-1", "a-1", 14) && run.feedback.Contains("방향이 반대"),
    "LED polarity reversal receives specific feedback");
Check(SubmitPair(run, "a-1", "b-1", 16) && SubmitPair(run, "b-2", "gnd-2", 18) &&
    run.connections.Count == 3 && run.correctionSeconds == 8 && run.wrongAttempts == 3,
    "corrected wiring installs three links and times each correction once");
Check(!GuideRules.Confirm(run, start.AddSeconds(19), true) && !run.lit &&
    GuideRules.Confirm(run, start.AddSeconds(20)) && !run.completed,
    "pre-power inspection precedes even simulated lighting");
Check(GuideRules.Confirm(run, start.AddSeconds(21), true) && run.completed && run.lit &&
    run.stepIndex == 5, "validated virtual circuit powers on and completes the run");

var physical = GuideRules.NewRun(guide, true, start);
Check(GuideRules.Confirm(physical, start) && !GuideRules.SelectPoint(physical, "plus-1") &&
    GuideRules.Confirm(physical, start.AddSeconds(1)), "physical progress records direct confirmation only");
GuideRules.RequestHelp(physical);
GuideRules.Confirm(physical, start.AddSeconds(2));
GuideRules.Confirm(physical, start.AddSeconds(3));
GuideRules.Confirm(physical, start.AddSeconds(4));
Check(!GuideRules.Confirm(physical, start.AddSeconds(5)) && !physical.lit && !physical.wiringChecked &&
    physical.helpRequests == 1, "unlit physical circuit returns to inspection and records help");
Check(GuideRules.Confirm(physical, start.AddSeconds(6)) && GuideRules.Confirm(physical, start.AddSeconds(7), true) &&
    physical.completed && physical.lit, "physical completion requires explicit observation of light after inspection");
GuideTransferChecks.Run(Check);
BoardRegistrationChecks.Run();
Console.WriteLine($"{passed} core checks passed.");
