using System;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCP;

public partial class CommandExecutor
{
    private string _uiRun="";
    public bool HasActiveGameplayAction => FarmActive || _activeShopExit!=null || _currentPath!=null
        || _moveCommandId!=null || _toolCommandId!=null || _holdToolCommandId!=null || _exitHouseActive
        || !_commandQueue.IsEmpty
        || (Context.IsWorldReady && (Game1.player.UsingTool || (!Game1.player.CanMove && Game1.activeClickableMenu==null)));
    public void BeginUiRun(string id) {StopUiRun();_uiRun=id;}
    private bool AllowUiCommand(GameCommand command)
    {
        return _uiRun!="" && command.Params.TryGetValue("_uiRun",out var token)
            && GetStringParam(token)==_uiRun
            && !command.Action.StartsWith("cheat",StringComparison.OrdinalIgnoreCase);
    }
    // Called on the game thread. Late commands from an old run are rejected.
    public void StopUiRun(string reason="UI_RUN_ENDED: inspect current state before continuing")
    {
        CancelSleepTransition();
        _uiRun="";
        CancelShopExit("UI run stopped");
        ClearMovementState();
        _exitHouseActive=false;
        _toolUseRemaining=0;_toolCallback=null;_toolCommandId=null;
        _holdToolRemaining=0;_holdToolCallback=null;_holdToolCommandId=null;_holdToolReleased=true;
        if(FarmActive) FinishFarm(_farmJob!,"PAUSED",reason);
        while(_commandQueue.TryDequeue(out var pending)) {
            try {pending.OnComplete?.Invoke(new CommandResponse {Id=pending.Id,Success=false,Message="UI run stopped"});}
            catch(Exception ex) {_monitor.Log(ex.Message,LogLevel.Trace);}
        }
    }
}
