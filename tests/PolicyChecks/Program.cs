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
Console.WriteLine("13 policy regression checks passed.");
