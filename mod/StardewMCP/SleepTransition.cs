using System;
using System.Reflection;
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
        if(!sleepAnswered || sleepStartDate==null || sleepTransitionError!="") return;
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
            if(Game1.player.CanMove && Game1.player.hasMoved && timePasses) {
                morningReady=true;
                _monitor.Log($"[MORNING VERIFIED] attempts={morningInputAttempts}, newDay={Game1.newDay}, time={Game1.timeOfDay}, tile=({(int)Game1.player.Tile.X},{(int)Game1.player.Tile.Y})",LogLevel.Info);
                return;
            }
            if(morningInputAttempts>=8) {
                sleepTransitionError=$"MORNING_INPUT_NOT_VERIFIED: newDay={Game1.newDay}, canMove={Game1.player.CanMove}, hasMoved={Game1.player.hasMoved}, shouldTimePass={timePasses}";
                _monitor.Log(sleepTransitionError,LogLevel.Warn);return;
            }
            // Normal configured movement input releases the game's morning input
            // gate. Game1.newDay is expected to still be true at this gate, so it
            // must not suppress the input. Cycle directions so a wall beside the
            // bed cannot prevent all real movement. Do not mutate game flags/time.
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
