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
            if(menu!=null || !Context.IsWorldReady || Game1.eventUp || Game1.globalFade || Game1.newDay) return;
            if(SleepDate()==sleepStartDate || Game1.timeOfDay<600 || Game1.timeOfDay>=1200
                || Game1.currentLocation is not FarmHouse) return;
            if(Game1.player.CanMove && Game1.player.hasMoved && Game1.shouldTimePass()) {morningReady=true;return;}
            if(morningInputAttempts>=5) {sleepTransitionError="MORNING_INPUT_NOT_VERIFIED";return;}
            // Normal configured movement input releases the game's morning input
            // gate; do not set CanMove, time, pause flags, or player position.
            var input=Game1.options.moveLeftButton;
            _helper.Input.Press(input.Length>0?input[0].ToSButton():SButton.A);
            morningInputAttempts++;
            nextSleepInput=DateTime.UtcNow.AddSeconds(1);
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
