using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using System.Reflection;

namespace StardewMCP;

public class AgentUiConfig
{
    public string ServerPath {get;set;}="agent/stardew-mcp-ingame.exe";
    public string CopilotCliPath {get;set;}="";
    public SButton OpenKey {get;set;}=SButton.F6;
    public SButton CancelKey {get;set;}=SButton.F7;
    public SButton ResetAiCallCountKey {get;set;}=SButton.F8;
    public bool EnableAutomaticBedtimeAlarm {get;set;}=true;
    public int FirstBedtimeAlarm {get;set;}=2200;
    public int SecondBedtimeAlarm {get;set;}=2400;
    public int FinalBedtimeAlarm {get;set;}=2500;
}

public sealed class IngameAgent
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly CommandExecutor executor;
    private readonly ConcurrentQueue<(int epoch,string line)> incoming=new();
    private readonly string logPath;
    private Process? host;
    private int epoch;
    private string run="";
    private DateTime stopAt;
    private bool stopping;
    private string pendingBedtimeGoal="";
    private int pendingBedtimeLevel;
    private int bedtimeDate=-1;
    private int bedtimeAlarmLevel;
    private string activeGoalText="";
    private string alarmOriginalGoal="";
    public AgentUiConfig Config {get;}
    public bool Ready {get;private set;}
    public bool Busy => run!="";
    public int AiCallCount {get;private set;}
    public string Result {get;private set;}="아직 실행한 작업이 없습니다.";
    public string Status {get;private set;}="연결 준비 중";
    public string LastGoal {get;private set;}="현재 위치와 에너지만 확인하고 보고해줘. 이동하거나 도구를 사용하지 마.";
    public void ClearGoalDraft() { LastGoal=""; }
    public void ResetAiCallCount()
    {
        AiCallCount=0;
        if(Context.IsWorldReady) Game1.addHUDMessage(new HUDMessage("AI 호출 카운트를 0으로 초기화했습니다."));
    }

    public IngameAgent(IModHelper helper,IMonitor monitor,CommandExecutor executor)
    {
        this.helper=helper;this.monitor=monitor;this.executor=executor;
        Config=helper.ReadConfig<AgentUiConfig>();
        if(Config.FirstBedtimeAlarm<1800 || Config.FinalBedtimeAlarm>2500
            || Config.FirstBedtimeAlarm>=Config.SecondBedtimeAlarm || Config.SecondBedtimeAlarm>=Config.FinalBedtimeAlarm) {
            Config.FirstBedtimeAlarm=2200;Config.SecondBedtimeAlarm=2400;Config.FinalBedtimeAlarm=2500;
            helper.WriteConfig(Config);
        }
        Directory.CreateDirectory(Path.Combine(helper.DirectoryPath,"logs"));
        logPath=Path.Combine(helper.DirectoryPath,"logs","ingame-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt");
    }
    private void Record(string text)
    {
        try {File.AppendAllText(logPath,text+Environment.NewLine,Encoding.UTF8);}
        catch(Exception ex) {monitor.Log("UI log: "+ex.Message,LogLevel.Trace);}
    }
    private static System.Collections.Generic.IEnumerable<string> ChatChunks(string text)
    {
        text=(text??"").Replace("AI 보고:","").Trim();
        foreach(string raw in text.Replace("\r","").Split('\n')) {
            string line=raw.Trim();
            if(line.Length==0) continue;
            while(line.Length>180) {yield return line.Substring(0,180);line=line.Substring(180);}
            if(line.Length>0) yield return line;
        }
    }
    private void PostResultToChat(string text)
    {
        try {
            object? chat=typeof(Game1).GetField("chatBox",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)?.GetValue(null)
                ?? typeof(Game1).GetProperty("chatBox",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)?.GetValue(null);
            if(chat==null) throw new InvalidOperationException("게임 채팅 객체가 없습니다.");
            MethodInfo? info=chat.GetType().GetMethod("addInfoMessage",new[]{typeof(string)});
            MethodInfo? colored=chat.GetType().GetMethod("addMessage",new[]{typeof(string),typeof(Microsoft.Xna.Framework.Color)});
            bool first=true;
            foreach(string chunk in ChatChunks(text)) {
                string message=(first?"[AI] ":"")+chunk;first=false;
                if(info!=null) info.Invoke(chat,new object[]{message});
                else if(colored!=null) colored.Invoke(chat,new object[]{message,Microsoft.Xna.Framework.Color.Cyan});
                else throw new MissingMethodException("채팅 메시지 메서드를 찾지 못했습니다.");
            }
        } catch(Exception ex) {
            monitor.Log("Chat output failed: "+ex.Message,LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage("AI 답변은 F6 결과창과 로그에서 확인하세요."));
        }
    }
    private static string AddSnapshotToBareCompletion(string text)
    {
        string normalized=(text??"").Replace("AI 보고:","").Trim();
        if(!normalized.Equals("GOAL COMPLETE",StringComparison.OrdinalIgnoreCase)) return text;
        var tile=Game1.player.Tile;
        return "AI 보고: GOAL COMPLETE\n완료 시점: 지역="+(Game1.currentLocation?.Name??"Unknown")
            +", 좌표=("+(int)tile.X+","+(int)tile.Y+"), 에너지="+(int)Game1.player.Stamina
            +"/"+(int)Game1.player.MaxStamina+", 시간="+Game1.timeOfDay;
    }
    private static string CompactLiveStatus(string line)
    {
        int farm=line.IndexOf("[FARM TASK]",StringComparison.Ordinal);
        if(farm>=0) {
            string value=line.Substring(farm+11).Trim();
            int id=value.IndexOf(" id=",StringComparison.Ordinal);
            if(id>=0) value=value.Substring(0,id);
            return "농사 작업 · "+value;
        }
        if(line.Contains("[AGENT THOUGHT]")) return "AI가 최종 답변을 작성했습니다.";
        if(line.Contains("[TASK BLOCKED]")) return "작업 중단 결과를 확인했습니다.";
        if(line.Contains("Failed")) return "실행 오류 · 자세한 내용은 최종 결과를 확인하세요.";
        return "AI 작업 진행 중";
    }
    public void StartHost()
    {
        if(host!=null) return;
        if(Context.IsMultiplayer) {Status="첫 버전은 싱글플레이만 지원합니다.";return;}
        try {
            string exe=Path.GetFullPath(Path.Combine(helper.DirectoryPath,Config.ServerPath));
            var psi=new ProcessStartInfo(exe) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
                RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
                StandardInputEncoding=new UTF8Encoding(false),WorkingDirectory=Path.GetDirectoryName(exe)!
            };
            psi.ArgumentList.Add("--ingame-host");
            if(!string.IsNullOrWhiteSpace(Config.CopilotCliPath)) psi.Environment["COPILOT_CLI_PATH"]=Config.CopilotCliPath;
            int generation=++epoch;
            host=new Process {StartInfo=psi};
            host.OutputDataReceived+=(_,e)=>{if(e.Data!=null) incoming.Enqueue((generation,e.Data));};
            host.ErrorDataReceived+=(_,e)=>{if(e.Data!=null) incoming.Enqueue((generation,JsonSerializer.Serialize(new {type="host_error",text=e.Data})));};
            host.Start();host.BeginOutputReadLine();host.BeginErrorReadLine();Status="서버 연결 중";
        } catch(Exception ex) {host?.Dispose();host=null;Ready=false;Status="서버 시작 실패: "+ex.Message;Record(Status);}
    }
    private void Send(object message)
    {
        if(host==null || host.HasExited) throw new IOException("서버가 연결되지 않았습니다.");
        host.StandardInput.WriteLine(JsonSerializer.Serialize(message));host.StandardInput.Flush();
    }
    private bool StartGoal(string goal,bool remember,bool automatic)
    {
        if(Busy || !Ready || !Context.IsWorldReady || Game1.player.UsingTool || !Game1.player.CanMove) {
            Status="서버 연결과 캐릭터의 이동 가능한 상태를 확인하세요.";return false;
        }
        goal=goal.Trim();
        if(goal.Length==0 || goal.Length>4000 || goal.Contains("[SLEEP_VERIFIED]")) {Status="목표를 1~4000자로 입력하세요. 이전 특수 토큰은 지원하지 않습니다.";return false;}
        if(remember) LastGoal=goal;
        activeGoalText=goal;Result=automatic?"시간 알람에 따라 AI가 주변 상황을 확인하고 판단 중입니다.":"실행 중";
        run=Guid.NewGuid().ToString("N");stopping=false;
        executor.BeginUiRun(run);
        try {Send(new {type="start",id=run,goal});Status="목표 접수 중";Record("목표: "+goal);return true;}
        catch(Exception ex) {Shutdown();Status=ex.Message;Record(Status);return false;}
    }
    public bool Submit(string goal) => StartGoal(goal,true,false);

    public bool SubmitGoalAnswer(string goalId,string questionId,string question,string answer)
    {
        answer=(answer??"").Trim();
        if(answer.Length is < 1 or > 2000) {Status="답변을 1~2000자로 입력하세요.";return false;}
        if(Busy) {Status="현재 작업을 종료한 뒤 답변하세요.";return false;}
        try {
            executor.AnswerGoalQuestion(goalId,questionId,answer);
            string continuation="CONTINUE PERSISTENT GOAL\nGoal ID: "+goalId+"\nThe user answered the dedicated in-game question. "
                +"First call inspect_long_term_goal for this exact ID, treat the saved answer as user-authorized context, and continue only within the stored goal constraints.\n"
                +"Question: "+question+"\nUser answer: "+answer;
            if(StartGoal(continuation,false,false)) return true;
            Status="답변은 저장됐지만 AI 재개를 시작하지 못했습니다. F6에서 목표를 다시 요청하세요.";
            return true;
        } catch(Exception ex) {Status="목표 답변 저장 실패: "+ex.Message;Record(Status);return false;}
    }

    public bool SubmitAgentAnswer(string questionId,string answer)
    {
        answer=(answer??"").Trim();
        if(answer.Length is < 1 or > 2000) {Status="답변을 1~2000자로 입력하세요.";return false;}
        if(Busy) {Status="현재 작업을 종료한 뒤 답변하세요.";return false;}
        try {
            AgentQuestion question=executor.AnswerAgentQuestion(questionId);
            string continuation="CONTINUE AFTER DEDICATED PLAYER ANSWER\nOriginal task context: "+question.ContinuationContext
                +"\nQuestion: "+question.Prompt+"\nUser answer: "+answer
                +"\nInspect live state and continue the original task. Do not ask this question again.";
            if(StartGoal(continuation,false,false)) return true;
            Status="답변을 받았지만 AI 재개를 시작하지 못했습니다. F6에서 다시 요청하세요.";return true;
        } catch(Exception ex) {Status="답변 처리 실패: "+ex.Message;Record(Status);return false;}
    }

    private int CurrentBedtimeLevel(int time)
    {
        if(time>=Config.FinalBedtimeAlarm) return 3;
        if(time>=Config.SecondBedtimeAlarm) return 2;
        if(time>=Config.FirstBedtimeAlarm) return 1;
        return 0;
    }
    private string BedtimeGoal(int level)
    {
        string handling=level==1
            ?"The current atomic gameplay action was allowed to finish before this alarm interrupted the goal."
            :"The active goal and gameplay action were forcibly interrupted for this alarm.";
        var tile=Game1.player.Tile;
        return "AUTOMATIC TIME DECISION ALARM\n"
            +"Alarm level="+level+", current game time="+Game1.timeOfDay+". "+handling+"\n"
            +"Snapshot: location="+(Game1.currentLocation?.Name??"Unknown")+", tile=("+(int)tile.X+","+(int)tile.Y+")"
            +", energy="+(int)Game1.player.Stamina+"/"+(int)Game1.player.MaxStamina+".\n"
            +"Interrupted original goal: "+(string.IsNullOrWhiteSpace(alarmOriginalGoal)?"none":alarmOriginalGoal)+"\n"
            +"First call get_surroundings and assess_daily_status. Then use the observed surroundings, route, remaining energy, time and unfinished work to decide whether to continue useful work or return home and sleep. "
            +"Returning home is NOT mandatory and no code has chosen it for you. Make and execute the decision yourself, and report the reason.";
    }
    private void InterruptForBedtimeAlarm()
    {
        if(!Busy || stopping) return;
        executor.StopUiRun("TIME_ALARM");stopping=true;stopAt=DateTime.UtcNow;
        Status="시간 알람 · 기존 작업 중단 후 AI 판단 대기";
        try {Send(new {type="cancel",id=run});} catch(Exception ex) {Record(ex.Message);Shutdown();}
    }
    private void TryStartPendingBedtime()
    {
        if(string.IsNullOrWhiteSpace(pendingBedtimeGoal) || stopping || !Context.IsWorldReady) return;
        if(Busy) {
            if(pendingBedtimeLevel==1 && !executor.HasActiveGameplayAction) InterruptForBedtimeAlarm();
            return;
        }
        if(Game1.activeClickableMenu!=null) return; // Never dismiss a profession, save, or held-item menu.
        if(Game1.player.UsingTool || !Game1.player.CanMove) return;
        string goal=pendingBedtimeGoal;pendingBedtimeGoal="";pendingBedtimeLevel=0;
        StartGoal(goal,false,true);
    }
    public void CheckBedtimeAlarm()
    {
        if(!Config.EnableAutomaticBedtimeAlarm || !Context.IsWorldReady) return;
        int today=(int)Game1.stats.DaysPlayed;
        if(bedtimeDate!=today) {bedtimeDate=today;bedtimeAlarmLevel=0;pendingBedtimeGoal="";pendingBedtimeLevel=0;alarmOriginalGoal="";}
        int level=CurrentBedtimeLevel(Game1.timeOfDay);
        if(level<=bedtimeAlarmLevel) return;
        if(Busy && string.IsNullOrWhiteSpace(alarmOriginalGoal)) alarmOriginalGoal=activeGoalText;
        bedtimeAlarmLevel=level;pendingBedtimeLevel=level;pendingBedtimeGoal=BedtimeGoal(level);
        Game1.addHUDMessage(new HUDMessage("시간 알람: AI가 계속 작업할지 귀가할지 판단합니다."));
        if(Busy && level>=2) InterruptForBedtimeAlarm();
        TryStartPendingBedtime();
    }
    public void ResetBedtimeAlarm()
    {
        bedtimeDate=-1;bedtimeAlarmLevel=0;pendingBedtimeGoal="";pendingBedtimeLevel=0;alarmOriginalGoal="";
    }
    public void Cancel()
    {
        executor.SuspendPendingTreeResume();
        executor.PauseActiveLongTermGoalsByUser();
        if(!Busy || stopping) return;
        executor.StopUiRun();stopping=true;stopAt=DateTime.UtcNow;
        Status="입력 차단됨 · 작업 종료 대기 중";Result="사용자가 취소했습니다. 이미 바뀐 게임 상태는 유지됩니다.";
        try {Send(new {type="cancel",id=run});} catch(Exception ex) {Record(ex.Message);Shutdown();}
    }
    public void Tick()
    {
        while(incoming.TryDequeue(out var entry)) {
            if(entry.epoch!=epoch) continue;
            try {
                using var doc=JsonDocument.Parse(entry.line);var root=doc.RootElement;
                string type=root.GetProperty("type").GetString()??"";
                string id=root.TryGetProperty("id",out var i)?i.GetString()??"":"";
                string text=root.TryGetProperty("text",out var t)?t.GetString()??"":"";
                if(type=="host_error") {Record(text);Status=text;continue;}
                if(type=="ready") {Ready=true;Status="지시 대기 중 · "+text;Record(text);continue;}
                if(type=="ai_call") {AiCallCount++;Record("AI 호출 카운트: "+AiCallCount);continue;}
                if(id!=run || run=="") continue;
                if(type=="done" || type=="rejected") {
                    executor.StopUiRun();run="";stopping=false;
                    activeGoalText="";
                    Status="작업 종료 · 새 목표를 입력할 수 있습니다";
                    if(Result=="실행 중") Result="작업이 종료되었습니다: "+text;
                    Result=AddSnapshotToBareCompletion(Result);
                    bool waitingForGoalInput=executor.TryGetPendingGoalQuestion(out _,out GoalQuestion? pendingQuestion) && pendingQuestion!=null;
                    bool waitingForAgentInput=executor.TryGetPendingAgentQuestion(out AgentQuestion? agentQuestion) && agentQuestion!=null;
                    if(waitingForGoalInput || waitingForAgentInput) {
                        Status="AI가 전용 질문창에서 사용자 답변을 기다립니다.";
                        Result="전용 질문창에서 답변하면 같은 작업 문맥으로 계속됩니다.";
                    } else if(executor.HasPendingGoalPlanAutomation()) {
                        Status="장기 목표의 다음 체크포인트를 자동 실행할 예정입니다.";
                    } else if(string.IsNullOrWhiteSpace(pendingBedtimeGoal)) PostResultToChat(Result);
                    Record(text);continue;
                }
                if(type=="started") Status="AI 시작 중";
                if(type=="log") {
                    Record(text);
                    int thought=text.IndexOf("[AGENT THOUGHT]",StringComparison.Ordinal);
                    if(thought>=0 && !stopping) Result="AI 보고: "+text.Substring(thought+15).Trim();
                    else if(!stopping && Result.StartsWith("AI 보고:") && !text.StartsWith("20") && !text.Contains("[UI WORKER")) {
                        if(Result.Length<2000) Result+="\n"+text;
                    }
                    if(text.Contains("Failed to start")) Result=text;
                    if(text.Contains("[FARM TASK]") || text.Contains("[AGENT THOUGHT]") || text.Contains("[TASK BLOCKED]") || text.Contains("Failed")) Status=CompactLiveStatus(text);
                }
            } catch(Exception ex) {monitor.Log("UI message: "+ex.Message,LogLevel.Trace);}
        }
        if(host!=null && host.HasExited) {Shutdown();Status="서버 종료됨 · 명령창에서 재연결하세요";}
        if(stopping && (DateTime.UtcNow-stopAt).TotalSeconds>10) {Shutdown();Status="작업 종료 지연으로 서버 정리됨 · 재연결 필요";}
        if(host==null && (!string.IsNullOrWhiteSpace(pendingBedtimeGoal)
            || executor.GetDueGoalWakeupPrompt()!="" || executor.HasPendingGoalPlanAutomation())) StartHost();
        TryStartPendingBedtime();
        if(!Busy && Ready && Context.IsWorldReady && Game1.activeClickableMenu==null
            && !Game1.eventUp && !Game1.player.UsingTool && Game1.player.CanMove
            && string.IsNullOrWhiteSpace(pendingBedtimeGoal)) {
            string wakeup=executor.GetDueGoalWakeupPrompt();
            if(wakeup!="" && StartGoal(wakeup,false,true)) executor.MarkGoalWakeupDispatched();
            else {
                string dayAdvance=executor.GetPendingGoalPlanDayAdvancePrompt();
                if(dayAdvance!="" && StartGoal(dayAdvance,false,true)) executor.MarkGoalPlanDayAdvanceDispatched();
                else {
                    string planResume=executor.GetPendingGoalPlanExecutionPrompt();
                    if(planResume!="" && StartGoal(planResume,false,true)) executor.MarkGoalPlanExecutionDispatched();
                    else {
                        string resume=executor.GetPendingTreeGoal();
                        if(resume!="" && StartGoal(resume,false,true)) executor.MarkPendingTreeDispatched();
                    }
                }
            }
        }
    }
    public void Shutdown()
    {
        executor.StopUiRun();++epoch;Ready=false;run="";stopping=false;activeGoalText="";CloseHost();
    }
    public void CloseHost()
    {
        if(host==null) return;
        var old=host;host=null;
        try {if(!old.HasExited) {old.StandardInput.WriteLine("{\"type\":\"shutdown\"}");old.StandardInput.Flush();}}
        catch(Exception) {}
        System.Threading.Tasks.Task.Run(()=>{try {if(!old.WaitForExit(3000)) old.Kill(true);}catch(Exception){}finally{old.Dispose();}});
    }
}
