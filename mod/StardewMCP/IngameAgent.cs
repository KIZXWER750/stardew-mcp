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
    public AgentUiConfig Config {get;}
    public bool Ready {get;private set;}
    public bool Busy => run!="";
    public string Result {get;private set;}="아직 실행한 작업이 없습니다.";
    public string Status {get;private set;}="연결 준비 중";
    public string LastGoal {get;private set;}="현재 위치와 에너지만 확인하고 보고해줘. 이동하거나 도구를 사용하지 마.";
    public void ClearGoalDraft() { LastGoal=""; }

    public IngameAgent(IModHelper helper,IMonitor monitor,CommandExecutor executor)
    {
        this.helper=helper;this.monitor=monitor;this.executor=executor;
        Config=helper.ReadConfig<AgentUiConfig>();
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
    public bool Submit(string goal)
    {
        if(Busy || !Ready || !Context.IsWorldReady || Game1.player.UsingTool || !Game1.player.CanMove) {
            Status="서버 연결과 캐릭터의 이동 가능한 상태를 확인하세요.";return false;
        }
        goal=goal.Trim();
        if(goal.Length==0 || goal.Length>4000 || goal.Contains("[SLEEP_VERIFIED]")) {Status="목표를 1~4000자로 입력하세요. 이전 특수 토큰은 지원하지 않습니다.";return false;}
        LastGoal=goal;Result="실행 중";run=Guid.NewGuid().ToString("N");stopping=false;
        executor.BeginUiRun(run);
        try {Send(new {type="start",id=run,goal});Status="목표 접수 중";Record("목표: "+goal);return true;}
        catch(Exception ex) {Shutdown();Status=ex.Message;Record(Status);return false;}
    }
    public void Cancel()
    {
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
                if(type=="ready") {Ready=true;Status="지시 대기 중";continue;}
                if(id!=run || run=="") continue;
                if(type=="done" || type=="rejected") {
                    executor.StopUiRun();run="";stopping=false;
                    Status="작업 종료 · 새 목표를 입력할 수 있습니다";
                    if(Result=="실행 중") Result="작업이 종료되었습니다: "+text;
                    Result=AddSnapshotToBareCompletion(Result);
                    PostResultToChat(Result);
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
    }
    public void Shutdown()
    {
        executor.StopUiRun();++epoch;Ready=false;run="";stopping=false;CloseHost();
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
