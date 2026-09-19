using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP;

public partial class CommandExecutor
{
    private DateTime nextSleepInput;
    private string sleepTransitionError="";
    private bool morningReady;
    private int morningInputAttempts;
    private bool morningGateObserved;
    private DateTime morningControllableAt;
    private int morningTransitionWaits;
    private Vector2? morningStartPosition;
    private int morningStartTime;
    private bool morningMovementVerified;
    public void CancelSleepTransition() {sleepAnswered=false;sleepStartDate=null;}

    public void ProcessOvernightCommands()
    {
        if(Context.IsWorldReady) return;
        if(_commandQueue.TryDequeue(out var command)) {
            if(command.Action=="sleep_step" && sleepAnswered && !command.Params.ContainsKey("reset")) ExecuteCommand(command);
            else command.OnComplete?.Invoke(new CommandResponse {Id=command.Id,Success=false,Message="World not ready; only an active sleep transition may be polled"});
        }
    }

    // Night menus can run while Context.IsWorldReady is false. Only this
    // allowlisted transition helper runs then, never general game commands.
    public void UpdateSleepTransition()
    {
        if(!sleepAnswered || sleepStartDate==null || sleepTransitionError!="" || morningReady) return;
        if((DateTime.UtcNow-sleepStarted).TotalSeconds>180) {
            sleepTransitionError="OVERNIGHT_TIMEOUT: overnight transition not verified";return;
        }
        if(DateTime.UtcNow<nextSleepInput) return;
        try {
            var menu=Game1.activeClickableMenu;
            if(menu is LevelUpMenu level) {
                if(ReadSleepMember<bool>(level,"isProfessionChooser")) {
                    sleepTransitionError="PROFESSION_CHOICE_REQUIRED: select a profession manually";
                    _monitor.Log(sleepTransitionError,LogLevel.Warn);return;
                }
                if(ReadSleepMember<int>(level,"timerBeforeStart")>0 || !level.readyToClose()) return;
                if(ReadSleepMember<bool>(level,"informationUp")) {
                    // The game's normal OK handler grants recipes/level benefits.
                    // receiveLeftClick is a no-op in this game build.
                    var ok=level.GetType().GetMethod("okButtonClicked",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                    if(ok==null) {sleepTransitionError="LEVEL_UP_CONFIRM_UNAVAILABLE";return;}
                    ok.Invoke(level,null);
                } else {
                    var star=ReadSleepMember<ClickableTextureComponent>(level,"starIcon");
                    if(star==null) {sleepTransitionError="LEVEL_UP_BUTTON_UNAVAILABLE";return;}
                    Game1.setMousePosition(star.bounds.Center.X,star.bounds.Center.Y);
                    _helper.Input.Press(SButton.MouseLeft);
                }
                nextSleepInput=DateTime.UtcNow.AddMilliseconds(800);return;
            }
            if(menu is ShippingMenu shipping) {
                var button=ReadSleepMember<ClickableTextureComponent>(shipping,"okButton");
                if(button==null) {sleepTransitionError="SHIPPING_CONFIRM_UNAVAILABLE";return;}
                shipping.receiveLeftClick(button.bounds.Center.X,button.bounds.Center.Y);
                nextSleepInput=DateTime.UtcNow.AddMilliseconds(800);return;
            }
            // SaveGameMenu must finish saving naturally. Unknown menus are never dismissed.
            if(menu!=null || !Context.IsWorldReady || Game1.eventUp || Game1.globalFade) return;
            if(SleepDate()==sleepStartDate || Game1.timeOfDay<600 || Game1.timeOfDay>=1200
                || Game1.currentLocation is not FarmHouse) return;
            bool timePasses=Game1.shouldTimePass();
            if(!morningGateObserved) {
                morningGateObserved=true;
                _monitor.Log($"[MORNING GATE] date={SleepDate()}, time={Game1.timeOfDay}, newDay={Game1.newDay}, canMove={Game1.player.CanMove}, hasMoved={Game1.player.hasMoved}, shouldTimePass={timePasses}",LogLevel.Info);
            }
            if(morningStartPosition.HasValue && !morningMovementVerified
                && Vector2.DistanceSquared(Game1.player.Position,morningStartPosition.Value)>=4f) {
                morningMovementVerified=true;
                _monitor.Log($"[MORNING MOVEMENT VERIFIED] start=({morningStartPosition.Value.X:0},{morningStartPosition.Value.Y:0}), current=({Game1.player.Position.X:0},{Game1.player.Position.Y:0})",LogLevel.Info);
            }
            if(Game1.player.CanMove && morningMovementVerified && timePasses && Game1.timeOfDay>morningStartTime) {
                morningReady=true;
                _monitor.Log($"[MORNING VERIFIED] attempts={morningInputAttempts}, newDay={Game1.newDay}, time={Game1.timeOfDay}, tile=({(int)Game1.player.Tile.X},{(int)Game1.player.Tile.Y}), positionChanged={morningMovementVerified}",LogLevel.Info);
                return;
            }
            // DayStarted fires before the wake-up fade has necessarily restored
            // player control. Inputs sent while CanMove is false are discarded by
            // the game, so don't consume the morning-input retry budget yet.
            if(!Game1.player.CanMove) {
                morningTransitionWaits++;
                if(morningTransitionWaits==1 || morningTransitionWaits%10==0)
                    _monitor.Log($"[MORNING TRANSITION WAIT] waits={morningTransitionWaits}, newDay={Game1.newDay}, fade={Game1.fadeToBlackAlpha:0.00}, globalFade={Game1.globalFade}, freezeControls={Game1.freezeControls}",LogLevel.Trace);
                nextSleepInput=DateTime.UtcNow.AddMilliseconds(500);
                return;
            }
            if(morningControllableAt==default) {
                morningControllableAt=DateTime.UtcNow;
                morningStartPosition=Game1.player.Position;
                morningStartTime=Game1.timeOfDay;
                _monitor.Log($"[MORNING CONTROLLABLE] date={SleepDate()}, time={Game1.timeOfDay}; sending normal movement input to start the clock.",LogLevel.Info);
            }
            if((DateTime.UtcNow-morningControllableAt).TotalSeconds>=30) {
                sleepTransitionError=$"MORNING_INPUT_NOT_VERIFIED: controllable for 30 seconds; newDay={Game1.newDay}, canMove={Game1.player.CanMove}, hasMoved={Game1.player.hasMoved}, shouldTimePass={timePasses}, attempts={morningInputAttempts}";
                _monitor.Log(sleepTransitionError,LogLevel.Warn);return;
            }
            if(morningMovementVerified) {
                nextSleepInput=DateTime.UtcNow.AddMilliseconds(250);
                return;
            }
            // Normal configured movement input releases the game's morning input
            // gate once the wake-up transition has restored player control. Cycle
            // directions so a wall beside the bed cannot prevent all real movement.
            // Do not mutate game flags, time, or CanMove directly.
            var configured=(morningInputAttempts%4) switch {
                0=>Game1.options.moveLeftButton,
                1=>Game1.options.moveRightButton,
                2=>Game1.options.moveDownButton,
                _=>Game1.options.moveUpButton
            };
            var fallback=(morningInputAttempts%4) switch {0=>SButton.A,1=>SButton.D,2=>SButton.S,_=>SButton.W};
            var morningButton=configured.Length>0?configured[0].ToSButton():fallback;
            _monitor.Log($"[MORNING INPUT] attempt={morningInputAttempts+1}, button={morningButton}, newDay={Game1.newDay}, canMove={Game1.player.CanMove}, hasMoved={Game1.player.hasMoved}",LogLevel.Info);
            _helper.Input.Press(morningButton);
            morningInputAttempts++;
            nextSleepInput=DateTime.UtcNow.AddMilliseconds(500);
        } catch(Exception ex) {
            sleepTransitionError="OVERNIGHT_CONFIRM_FAILED: "+ex.GetBaseException().Message;
            _monitor.Log(sleepTransitionError,LogLevel.Warn);
        }
    }

    private static T ReadSleepMember<T>(object instance,string name)
    {
        var field=instance.GetType().GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
        if(field==null) throw new MissingFieldException(instance.GetType().Name,name);
        return (T)field.GetValue(instance)!;
    }
}
