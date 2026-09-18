using System;
using System.Collections.Generic;
using System.Reflection;
using StardewValley;
using StardewValley.Menus;

namespace StardewMCP;

public partial class CommandExecutor
{
    private sealed class PierreStatus
    {
        public string Status {get;set;}="";
        public string Reason {get;set;}="";
        public int Time {get;set;}
        public int Day {get;set;}
        public string Season {get;set;}="";
        public bool Wednesday {get;set;}
        public bool CommunityCenterRestored {get;set;}
        public bool HasTownKey {get;set;}
        public bool? FestivalClosed {get;set;}
        public bool MenuOpen {get;set;}
        public bool CanAttemptTrade {get;set;}
        public int TradeOpens {get;set;}=900;
        public int TradeCloses {get;set;}=1700;
        public int BuildingCloses {get;set;}=2100;
        public string Authority {get;set;}="Vanilla schedule preflight; actual shop menu is final authority. Modded schedules may differ.";
    }
    private static bool ShopBool(object? value) => value is bool b?b:value!=null && ShopMember(value,"Value") is bool v && v;
    private PierreStatus ReadPierreStatus()
    {
        var status=new PierreStatus {Time=Game1.timeOfDay,Day=Game1.dayOfMonth,Season=Game1.currentSeason,Wednesday=(Game1.dayOfMonth-1)%7==2,
            MenuOpen=Game1.currentLocation.Name=="SeedShop" && Game1.activeClickableMenu is ShopMenu,
            HasTownKey=ShopBool(ShopMember(Game1.player,"hasTownKey"))};
        // Query the loaded community center rather than guessing progress from the weekday.
        foreach(var location in Game1.locations) if(location.Name=="CommunityCenter") {
            var method=location.GetType().GetMethod("areAllAreasComplete",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,Type.EmptyTypes,null);
            if(method?.ReturnType==typeof(bool)) status.CommunityCenterRestored=(bool)method.Invoke(location,null)!;
        }
        try {
            var dates=Game1.content.Load<Dictionary<string,string>>("Data/Festivals/FestivalDates");
            // Moonlight Jellies starts at night; daytime trading is available.
            status.FestivalClosed=dates.ContainsKey(Game1.currentSeason+Game1.dayOfMonth) && !(Game1.currentSeason=="summer" && Game1.dayOfMonth==28);
        } catch {status.FestivalClosed=null;}
        status.CanAttemptTrade=status.Time>=900 && status.Time<1700 && (!status.Wednesday || status.CommunityCenterRestored || status.HasTownKey) && status.FestivalClosed==false;
        status.Reason=status.Time<900?"BEFORE_09_00":status.Time>=1700?"AFTER_17_00":status.Wednesday && !status.CommunityCenterRestored && !status.HasTownKey?"WEDNESDAY_CLOSED":status.FestivalClosed==true?"FESTIVAL_CLOSED":status.FestivalClosed==null?"FESTIVAL_CALENDAR_UNAVAILABLE":"COUNTER_AVAILABILITY_NOT_YET_VERIFIED";
        status.Status=status.CanAttemptTrade?"SCHEDULE_OPEN":"CLOSED_OR_UNKNOWN";
        if(status.MenuOpen){status.Status="SHOP_MENU_OPEN";status.Reason="ACTUAL_SHOP_MENU_VERIFIED";status.CanAttemptTrade=true;}
        return status;
    }
    private CommandResponse ShopStatus(GameCommand c) => FarmReply(c,ReadPierreStatus());
}
