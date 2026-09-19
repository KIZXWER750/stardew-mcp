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
Console.WriteLine("24 policy, memory and knowledge regression checks passed.");
