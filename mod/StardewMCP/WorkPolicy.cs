using System;
using System.Collections.Generic;

namespace StardewMCP;

// Game-independent policies shared with executable regression tests.
public static class WorkPolicy
{
    public static bool CanResume(bool enabled,int time,int stop,float energy,int reserve,
        string savedDate,string today,string reason,float previousEnergy)
        => enabled && time<Math.Min(2200,stop) && energy>=reserve+24
            && (savedDate!=today || reason=="LOW_ENERGY" && energy>=previousEnergy+20);

    public static List<(int X,int Y)>? FindRoute(int width,int height,(int X,int Y) start,
        (int X,int Y) goal,Func<int,int,int?> cost,Func<(int X,int Y),(int X,int Y),bool> edge)
    {
        if(goal.X<0 || goal.Y<0 || goal.X>=width || goal.Y>=height) return null;
        var queue=new PriorityQueue<(int X,int Y),int>();
        var scores=new Dictionary<(int X,int Y),int>{{start,0}};
        var previous=new Dictionary<(int X,int Y),(int X,int Y)>();
        queue.Enqueue(start,0);
        while(queue.TryDequeue(out var current,out int priority)) {
            if(priority!=scores[current]) continue;
            if(current==goal) {
                var result=new List<(int X,int Y)>();
                while(current!=start) {result.Add(current);current=previous[current];}
                result.Reverse();return result;
            }
            foreach(var delta in new[]{(1,0),(-1,0),(0,1),(0,-1)}) {
                var next=(X:current.X+delta.Item1,Y:current.Y+delta.Item2);
                if(next.X<0 || next.Y<0 || next.X>=width || next.Y>=height || !edge(current,next)) continue;
                var step=cost(next.X,next.Y);
                if(!step.HasValue || step.Value<1) continue;
                int score=priority+step.Value;
                if(scores.TryGetValue(next,out int known) && known<=score) continue;
                scores[next]=score;previous[next]=current;queue.Enqueue(next,score);
            }
        }
        return null;
    }
}
