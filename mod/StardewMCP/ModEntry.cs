using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewMCP;

/// <summary>Main entry point for the Stardew MCP Bridge mod.</summary>
public class ModEntry : Mod
{
    private WebSocketServer? _wsServer;
    private GameStateSerializer? _stateSerializer;
    private CommandExecutor? _commandExecutor;
    private IngameAgent? _agentUi;

    /// <summary>The mod entry point.</summary>
    public override void Entry(IModHelper helper)
    {
        Monitor.Log("Stardew MCP Bridge loading...", LogLevel.Info);

        // Initialize components
        _stateSerializer = new GameStateSerializer();
        _commandExecutor = new CommandExecutor(helper, Monitor);
        _stateSerializer.SetCommandExecutor(_commandExecutor); // Wire up for movement state
        _wsServer = new WebSocketServer(Monitor, _stateSerializer, _commandExecutor);
        _agentUi = new IngameAgent(helper,Monitor,_commandExecutor);
        helper.Events.Input.ButtonPressed += (_,e) => {
            if(_agentUi==null) return;
            if(e.Button==_agentUi.Config.CancelKey) {Helper.Input.Suppress(e.Button);_agentUi.Cancel();}
            if(!Context.IsWorldReady) return;
            if(e.Button==_agentUi.Config.OpenKey && Game1.activeClickableMenu==null) {
                Helper.Input.Suppress(e.Button);
                if(_agentUi.Busy) Game1.addHUDMessage(new HUDMessage("작업 중입니다. 취소: "+_agentUi.Config.CancelKey));
                else if(_commandExecutor!=null && _commandExecutor.TryGetPendingGoalQuestion(out string goalId,out GoalQuestion? question) && question!=null)
                    Game1.activeClickableMenu=new GoalQuestionMenu(_agentUi,goalId,question);
                else Game1.activeClickableMenu=new AgentMenu(_agentUi);
            }
        };
        helper.Events.Display.RenderedHud += (_,e) => {
            if(!Context.IsWorldReady || _agentUi==null) return;
            if(_agentUi.Busy) {
                string text=_agentUi.Status;if(text.Length>120) text=text.Substring(0,120)+"…";
                string display=Game1.parseText("AI 작업\n"+text+"\n취소: "+_agentUi.Config.CancelKey,Game1.smallFont,580);
                var panel=new Microsoft.Xna.Framework.Rectangle(12,88,620,132);
                e.SpriteBatch.Draw(Game1.fadeToBlackRect,panel,Microsoft.Xna.Framework.Color.Black*0.82f);
                e.SpriteBatch.DrawString(Game1.smallFont,display,new Microsoft.Xna.Framework.Vector2(panel.X+14,panel.Y+12),Microsoft.Xna.Framework.Color.White);
            }
            string goalText=_commandExecutor?.GetLongTermGoalHudText()??"";
            if(goalText!="") {
                int panelWidth=500,panelHeight=142;
                var panel=new Microsoft.Xna.Framework.Rectangle(Game1.uiViewport.Width-panelWidth-18,Game1.uiViewport.Height-panelHeight-18,panelWidth,panelHeight);
                e.SpriteBatch.Draw(Game1.fadeToBlackRect,panel,Microsoft.Xna.Framework.Color.Black*0.78f);
                e.SpriteBatch.DrawString(Game1.smallFont,Game1.parseText(goalText,Game1.smallFont,panelWidth-28),
                    new Microsoft.Xna.Framework.Vector2(panel.X+14,panel.Y+12),Microsoft.Xna.Framework.Color.White);
            }
        };
        System.AppDomain.CurrentDomain.ProcessExit += (_,_) => _agentUi?.CloseHost();

        // Register events
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

        Monitor.Log("Stardew MCP Bridge loaded!", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        Monitor.Log("Game launched, starting WebSocket server on port 8765...", LogLevel.Info);
        _wsServer?.Start(8765);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _commandExecutor?.LoadLongTermMemory();
        _commandExecutor?.LoadPendingTrees();
        _commandExecutor?.LoadGameKnowledge();
        _agentUi?.StartHost();
        _agentUi?.CheckBedtimeAlarm();
        Monitor.Log($"Save loaded: {Game1.player.Name} on {Game1.player.farmName} Farm", LogLevel.Info);
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        _commandExecutor?.RefreshCurrentLocationChestMemory();
        _commandExecutor?.FlushLongTermMemory();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        _commandExecutor?.VerifyLongTermMemoryAfterDayChange();
    }

    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        _agentUi?.CheckBedtimeAlarm();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _commandExecutor?.UpdateSleepTransition();
        _commandExecutor?.ProcessOvernightCommands();
        _agentUi?.Tick();
        if(_agentUi!=null && _commandExecutor!=null && !_agentUi.Busy && Context.IsWorldReady && Game1.activeClickableMenu==null
            && !Game1.eventUp && Game1.player.CanMove
            && _commandExecutor.TryGetPendingGoalQuestion(out string goalId,out GoalQuestion? question) && question!=null && !question.Presented) {
            _commandExecutor.MarkGoalQuestionPresented(goalId,question.Id);
            Game1.activeClickableMenu=new GoalQuestionMenu(_agentUi,goalId,question);
        }
        // Only process when game is running
        if (!Context.IsWorldReady)
            return;

        // Process any pending commands from WebSocket
        _commandExecutor?.ProcessPendingCommands();
    }

    private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        // Only broadcast state when game is running
        if (!Context.IsWorldReady)
            return;

        // Detect manual chest moves and content changes without requiring an AI command.
        _commandExecutor?.RefreshCurrentLocationChestMemory();
        _commandExecutor?.RefreshLongTermGoalProgress();
        _commandExecutor?.RefreshGoalPlanFreshness();

        // Broadcast game state to connected clients
        _wsServer?.BroadcastState();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _commandExecutor?.FlushLongTermMemory();
        _commandExecutor?.ClearLongTermMemorySession();
        _commandExecutor?.ClearGameKnowledgeSession();
        _agentUi?.Shutdown();
        _agentUi?.ResetBedtimeAlarm();
        _commandExecutor?.CancelFarmOnTitle();
        Monitor.Log("Returned to title screen", LogLevel.Info);
    }
}
