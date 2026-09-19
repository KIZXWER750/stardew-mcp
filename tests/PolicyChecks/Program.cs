using System;
using System.Linq;
using StardewMCP;

static void Check(bool condition,string message) {if(!condition) throw new Exception(message);}
var direct=WorkPolicy.FindRoute(5,3,(0,1),(4,1),(x,y)=>1,(a,b)=>true)!;
Check(direct.Count==4,"Uniform-cost route must use four steps through removable obstacle");
var weighted=WorkPolicy.FindRoute(5,3,(0,1),(4,1),(x,y)=>x==2 && y==1?20:1,(a,b)=>true)!;
Check(weighted.Count==6 && !weighted.Contains((2,1)),"Normal weighted travel must prefer short detour");
var protectedRoute=WorkPolicy.FindRoute(5,3,(0,1),(4,1),(x,y)=>x==2?null:1,(a,b)=>true);
Check(protectedRoute==null,"Protected wall must never become a clearing route");
var edgeBlocked=WorkPolicy.FindRoute(3,1,(0,0),(2,0),(x,y)=>1,(a,b)=>b.X!=1);
Check(edgeBlocked==null,"Porch/map edge restrictions must remain enforced");
Check(WorkPolicy.FindRoute(3,1,(0,0),(3,0),(x,y)=>1,(a,b)=>true)==null,"Out of bounds");
Check(WorkPolicy.FindRoute(3,1,(0,0),(0,0),(x,y)=>1,(a,b)=>true)!.Count==0,"Already arrived");
Check(!WorkPolicy.CanResume(false,600,2200,270,20,"1","2","TIME_LIMIT",20),"F7 must disable resume");
Check(!WorkPolicy.CanResume(true,600,2200,25,20,"1","2","TIME_LIMIT",20),"Insufficient recovery");
Check(!WorkPolicy.CanResume(true,2200,2200,270,20,"1","2","TIME_LIMIT",20),"Late work forbidden");
Check(!WorkPolicy.CanResume(true,900,2200,270,20,"1","1","TIME_LIMIT",20),"No same-day timeout loop");
Check(WorkPolicy.CanResume(true,600,2200,270,20,"1","2","TIME_LIMIT",20),"Next-day resume");
Check(WorkPolicy.CanResume(true,900,2200,60,20,"1","1","LOW_ENERGY",23),"Recovered energy resume");
Check(!WorkPolicy.CanResume(true,900,2200,44,20,"1","1","LOW_ENERGY",30),"Tiny energy change must not loop");
var oldNotebook=MemorySchema.Normalize(new NotebookDocument {SchemaVersion=0,Chests=new() {
    new ChestMemory {Id="same",Location="Farm",TileX=1,TileY=2},
    new ChestMemory {Id="same",Location="Farm",TileX=3,TileY=4}
}});
Check(oldNotebook.SchemaVersion==1,"Version-zero notebook must migrate to schema v1");
Check(oldNotebook.Chests.Count==1 && oldNotebook.Chests[0].TileX==3,"Duplicate chest IDs must keep the newest record");
var oldTasks=MemorySchema.Normalize(new TaskDocument {SchemaVersion=0,Tasks=new() {
    new PersistentTask {Id="task",Status="PAUSED",Summary="resume later"}
}});
Check(oldTasks.SchemaVersion==1 && oldTasks.Tasks[0].Status==TaskStatuses.Paused,"Task migration must normalize status");
Check(MemorySchema.CanTransition(TaskStatuses.Paused,TaskStatuses.Active),"Paused task must be resumable");
Check(MemorySchema.CanTransition(TaskStatuses.Active,TaskStatuses.Completed),"Active task must be completable");
Check(!MemorySchema.CanTransition(TaskStatuses.Completed,TaskStatuses.Active),"Terminal task must not restart implicitly");
bool resumeModeRejected=false;
try {MemorySchema.NormalizeResumeMode("whenever");} catch(InvalidOperationException) {resumeModeRejected=true;}
Check(resumeModeRejected,"Unknown task resume modes must be rejected");
bool futureRejected=false;
try {MemorySchema.Normalize(new NotebookDocument {SchemaVersion=99});} catch(InvalidOperationException) {futureRejected=true;}
Check(futureRejected,"Unknown future schemas must be rejected for backup recovery");
var knowledge=KnowledgeSchema.Normalize(new WorldKnowledgeDocument {SchemaVersion=0,Routes=new() {
    new RouteEdgeKnowledge {From="Farm",To="BusStop",X=1,Y=1,Action="warp"},
    new RouteEdgeKnowledge {From="BusStop",To="Town",X=2,Y=2,Action="warp"},
    new RouteEdgeKnowledge {From="Farm",To="Forest",X=3,Y=3,Action="warp"}
}});
Check(knowledge.SchemaVersion==1,"Version-zero knowledge must migrate to schema v1");
var worldRoute=KnowledgeSchema.FindRoute(knowledge,"Farm","Town");
Check(worldRoute!=null && worldRoute.Count==2 && worldRoute[0].To=="BusStop","Knowledge route must preserve ordered graph hops");
Check(KnowledgeSchema.FindRoute(knowledge,"Town","Farm")==null,"Knowledge route must not invent reverse edges");
Check(KnowledgeSchema.FindRoute(knowledge,"Farm","Farm")!.Count==0,"Same-location knowledge route must be empty");
var wiki=System.Text.Json.JsonSerializer.Deserialize<WikiKnowledgeCollection>("{\"schemaVersion\":1,\"category\":\"crops\",\"entries\":[{\"subject\":\"Parsnip\",\"category\":\"crop\",\"facts\":{\"growthDays\":4},\"source\":{\"pageUrl\":\"https://stardewvalleywiki.com/Parsnip\",\"pageRevision\":123}}]}",new System.Text.Json.JsonSerializerOptions {PropertyNameCaseInsensitive=true});
Check(wiki?.Entries.Count==1 && wiki.Entries[0].Facts["growthDays"].GetInt32()==4 && wiki.Entries[0].Source.PageRevision==123,"Wiki cache must retain normalized facts and provenance");
var context=MemoryContextSelector.Select(new NotebookDocument {Chests=new() {
    new ChestMemory {Id="farm-chest",Location="Farm",Purpose=new ChestPurposeMemory {Value="wood storage"},Contents=new(){new ChestItemMemory{Name="Wood",Quantity=80}}},
    new ChestMemory {Id="town-chest",Location="Town",Purpose=new ChestPurposeMemory {Value="fish"}}
},Notes=new(){new MemoryNote{Id="rule",Kind="rule",Text="Keep hardwood"},new MemoryNote{Id="other",Kind="note",Text="Unrelated birthday"}}},
new TaskDocument {Tasks=new(){new PersistentTask{Id="open",Kind="trees",Summary="Collect wood",Status=TaskStatuses.Paused,Targets=new(){new TaskTarget{Location="Farm"}}},new PersistentTask{Id="done",Summary="Old wood task",Status=TaskStatuses.Completed}}},
"collect wood on the farm","Farm","spring-2",new MemoryRestoreReport{RoundTripVerified=true});
Check(context.Chests.Count==1 && context.Chests[0].MemoryId=="farm-chest","Goal context must select local or text-relevant chests only");
Check(context.Tasks.Count==1 && context.Tasks[0].Id=="open","Goal context must include unfinished work and exclude terminal tasks");
Check(context.Notes.Count==1 && context.Notes[0].Id=="rule","Goal context must retain user rules without injecting unrelated notes");
var persistedNotebook=new NotebookDocument{Revision=7,Chests=new(){new ChestMemory{Id="stable"}}};
var persistedTasks=new TaskDocument{Revision=9,Tasks=new(){new PersistentTask{Id="resume",Summary="Resume tomorrow"}}};
var restoredNotebook=MemorySchema.Normalize(System.Text.Json.JsonSerializer.Deserialize<NotebookDocument>(System.Text.Json.JsonSerializer.Serialize(persistedNotebook)));
var restoredTasks=MemorySchema.Normalize(System.Text.Json.JsonSerializer.Deserialize<TaskDocument>(System.Text.Json.JsonSerializer.Serialize(persistedTasks)));
Check(restoredNotebook.Revision==7 && restoredNotebook.Chests[0].Id=="stable" && restoredTasks.Revision==9 && restoredTasks.Tasks[0].Id=="resume","Memory serialization round trip must preserve revisions and stable IDs");
string signatureA=KnowledgeSchema.ComputeContentSignature(new[]{"map=Farm|tile=1","shop=SeedShop"});
string signatureB=KnowledgeSchema.ComputeContentSignature(new[]{"map=Farm|tile=2","shop=SeedShop"});
Check(signatureA!=signatureB && signatureA.Length==64,"Knowledge signature must invalidate when map inputs change");
var moneyGoal=new LongTermGoal{Id="money",Kind=GoalKinds.MoneyTarget,Status=GoalStatuses.Active,Summary="Reach 5000g",
    Money=new MoneyGoalSpec{Metric=MoneyGoalMetrics.CurrentBalance,TargetValue=5000,StartingMoney=1200}};
var moneyProgress=GoalSchema.EvaluateMoney(moneyGoal,3200,12,"spring-12-y1",900);
Check(moneyProgress.CurrentValue==3200 && moneyProgress.RemainingValue==1800 && !moneyProgress.SuccessConditionMet,"Current-balance goal must report verified remaining gold");
var achieved=GoalSchema.EvaluateMoney(moneyGoal,5100,12,"spring-12-y1",900);
Check(achieved.SuccessConditionMet && achieved.Percent==100,"Money goal must complete only when live balance reaches target");
moneyGoal.Money.Metric=MoneyGoalMetrics.BalanceIncrease;
var increase=GoalSchema.EvaluateMoney(moneyGoal,4000,12,"spring-12-y1",900);
Check(increase.CurrentValue==2800 && increase.RemainingValue==2200,"Balance-increase goal must use its captured starting balance");
moneyGoal.Money.DeadlineDayIndex=10;
var overdue=GoalSchema.EvaluateMoney(moneyGoal,2000,11,"spring-11-y1",900);
Check(overdue.DeadlineMissed,"Unmet goal after its deadline must be reported overdue");
Check(GoalSchema.CanTransition(GoalStatuses.Active,GoalStatuses.AwaitingUser),"Active goal must be able to wait for dedicated user input");
Check(GoalSchema.CanTransition(GoalStatuses.AwaitingUser,GoalStatuses.Active),"Answered goal must be resumable");
Check(!GoalSchema.CanTransition(GoalStatuses.Completed,GoalStatuses.Active),"Completed goal must not restart implicitly");
var goalDocument=GoalSchema.Normalize(System.Text.Json.JsonSerializer.Deserialize<GoalDocument>(System.Text.Json.JsonSerializer.Serialize(new GoalDocument{Revision=4,Goals=new(){moneyGoal}})));
Check(goalDocument.Revision==4 && goalDocument.Goals.Single().Id=="money","Goal JSON round trip must preserve revision and stable ID");
var parsnipProfit=CropProfitMath.Calculate(new CropProfitInput{GrowthDays=4,RegrowDays=-1,DaysRemaining=10,Tiles=10,SeedPrice=20,UnitSellPrice=35});
Check(parsnipProfit.Harvests==1 && parsnipProfit.UpfrontCost==200 && parsnipProfit.ExpectedRevenue==350 && parsnipProfit.ExpectedProfit==150,"Single-harvest crop economics must include seed cost");
var regrowProfit=CropProfitMath.Calculate(new CropProfitInput{GrowthDays=5,RegrowDays=3,DaysRemaining=12,Tiles=2,SeedPrice=50,UnitSellPrice=100,ExpectedYieldPerHarvest=1});
Check(regrowProfit.Harvests==3 && regrowProfit.ExpectedProfit==500,"Regrowing crops must count only harvests inside the season window");
var tooLate=CropProfitMath.Calculate(new CropProfitInput{GrowthDays=13,RegrowDays=-1,DaysRemaining=12,Tiles=4,SeedPrice=10,UnitSellPrice=100});
Check(tooLate.Harvests==0 && tooLate.ExpectedProfit==-40,"Too-late planting must not invent a harvest");
var quick=new GoalPlanCandidate{Id="sell",ExpectedGold=600,ExpectedProfit=600,Days=0,WorkUnits=4,Confidence="high"};
var profitable=new GoalPlanCandidate{Id="crop",ExpectedGold=1800,ExpectedProfit=1200,Days=5,WorkUnits=100,Confidence="medium"};
Check(GoalPlanPolicy.Select(new[]{quick,profitable},"fastest",1000)?.Id=="sell","Fastest strategy must prefer the earliest verified return");
Check(GoalPlanPolicy.Select(new[]{quick,profitable},"highest_profit",1000)?.Id=="crop","Highest-profit strategy must prefer net profit");
Check(GoalPlanPolicy.Select(new[]{quick,profitable},"balanced",1000)?.Id=="crop","Balanced strategy must prefer a candidate covering the remaining target");
var migratedGoalDocument=GoalSchema.Normalize(new GoalDocument{SchemaVersion=1,Goals=new(){new LongTermGoal{Id="planned",Summary="Plan",Money=new MoneyGoalSpec{TargetValue=100}}}});
Check(migratedGoalDocument.SchemaVersion==3 && migratedGoalDocument.Goals[0].Plan.Status==GoalPlanStatuses.None,"Goal schema v1 must migrate to execution plan schema v3");
Check(GoalPlanPolicy.RectangleForTiles(16)==(4,4),"Sixteen planned tiles must bind to a 4x4 rectangle");
Check(GoalPlanPolicy.RectangleForTiles(15).Width*GoalPlanPolicy.RectangleForTiles(15).Height==15,"Planned plot rectangle must preserve the requested tile count when factorable");
var executingGoal=new LongTermGoal{Id="executing",Summary="Execute",Money=new MoneyGoalSpec{TargetValue=5000},Plan=new GoalExecutionPlan{Status=GoalPlanStatuses.Waiting,Revision=3,
    Plot=new GoalPlanPlot{X=10,Y=12,Width=4,Height=4},Steps=new(){new GoalPlanStep{Id="step-01",Status=GoalPlanStepStatuses.InProgress,LeaseId="lease",AttemptCount=1}}}};
var restoredExecution=GoalSchema.Normalize(System.Text.Json.JsonSerializer.Deserialize<GoalDocument>(System.Text.Json.JsonSerializer.Serialize(new GoalDocument{SchemaVersion=3,Goals=new(){executingGoal}}))!).Goals.Single();
Check(restoredExecution.Plan.Status==GoalPlanStatuses.Waiting && restoredExecution.Plan.Plot?.X==10,"Persistent execution must retain waiting state and fixed plot");
Check(restoredExecution.Plan.Steps[0].LeaseId=="lease" && restoredExecution.Plan.Steps[0].AttemptCount==1,"Persistent execution must retain an in-progress lease without replaying it");
var migratedPlan=GoalSchema.Normalize(new GoalDocument{SchemaVersion=2,Goals=new(){new LongTermGoal{Id="old-plan",Summary="Old",Money=new MoneyGoalSpec{TargetValue=100},Plan=new GoalExecutionPlan{Status=GoalPlanStatuses.Ready}}}}).Goals.Single();
Check(migratedPlan.Plan.Status==GoalPlanStatuses.Stale && migratedPlan.Plan.BlockedReason=="MIGRATED_REPLAN_REQUIRED","Pre-execution plans must require one refresh after schema v3 migration");
Console.WriteLine("50 policy, memory, knowledge, goal and economy regression checks passed.");
