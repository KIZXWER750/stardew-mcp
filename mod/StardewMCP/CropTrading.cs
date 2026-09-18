using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewMCP;

public partial class CommandExecutor
{
    private sealed class CropQuote
    {
        public string Id="",Location="";
        public int Slot,Count,Quality;
        public Item Item=null!;
        public Chest? Chest;
        public IClickableMenu? Menu;
    }
    private readonly Dictionary<string,CropQuote> _cropQuotes=new();
    private readonly Dictionary<string,(string Location,Point Tile,Chest Chest)> _chestObservations=new();
    private readonly Dictionary<string,object> _cropReceipts=new();
    private static bool TradeCrop(Item? item) => item is StardewValley.Object && !ShopBool(ShopMember(item,"questItem")) && !ShopBool(ShopMember(item,"specialItem")) && (item.Category==-75 || item.Category==-79 || item.Category==-80);
    private static int TradeQuality(Item item) => item is StardewValley.Object obj?obj.Quality:0;
    private static void NoTradeModifiers()
    {
        var keys=Keyboard.GetState();
        if(new[]{Keys.LeftShift,Keys.RightShift,Keys.LeftControl,Keys.RightControl}.Any(keys.IsKeyDown)) throw new InvalidOperationException("Release Shift/Ctrl first");
    }
    private static List<Item?> TradeItems(object menu)
    {
        if(ShopMember(menu,"actualInventory") is not IEnumerable items) throw new InvalidOperationException("Unsupported inventory menu data");
        return items.Cast<object?>().Select(i=>i as Item).ToList();
    }
    private static int CountCrop(IEnumerable<Item?> items,string id,int? quality=null) => items.Where(i=>i!=null && i.QualifiedItemId==id && (!quality.HasValue || TradeQuality(i)==quality)).Sum(i=>i!.Stack);
    private CropQuote QuoteCrop(Item item,int slot,Chest? chest=null,IClickableMenu? menu=null)
    {
        if(_cropQuotes.Count>=512) _cropQuotes.Clear();
        var q=new CropQuote {Id=Guid.NewGuid().ToString("N"),Location=Game1.currentLocation.Name,Slot=slot,Count=item.Stack,Quality=TradeQuality(item),Item=item,Chest=chest,Menu=menu};
        _cropQuotes[q.Id]=q;return q;
    }
    private object CropRow(CropQuote q) => new {quoteId=q.Id,slot=q.Slot,itemId=q.Item.QualifiedItemId,name=q.Item.DisplayName,quantity=q.Count,quality=q.Quality,estimatedUnitPrice=((StardewValley.Object)q.Item).sellToStorePrice(),wholeStackOnly=true};
    private CropQuote RequireCropQuote(GameCommand c)
    {
        if(!_cropQuotes.TryGetValue(ShopText(c,"quote_id"),out var q)) throw new InvalidOperationException("Inspect crops again; quote missing");
        return q;
    }
    private string CropReceiptKey(CropQuote q,string operation,int keep,int minimum=0)
        => $"{_uiRun}|{operation}|{q.Location}|{q.Chest?.TileLocation.ToString()??"inventory"}|{q.Slot}|{q.Item.QualifiedItemId}|{q.Quality}|{q.Count}|{keep}|{minimum}";
    private CommandResponse InspectSellableCrops(GameCommand c)
    {
        var rows=new List<object>();
        for(int i=0;i<Game1.player.Items.Count;i++) if(TradeCrop(Game1.player.Items[i])) rows.Add(CropRow(QuoteCrop(Game1.player.Items[i],i)));
        return FarmReply(c,new {status="OBSERVED",inventory=rows,schedule=ReadPierreStatus(),note="Crop-category candidates only. Actual acceptance requires Pierre's open shop. Seeds/tools/resources are excluded. Retained food, gifts and quest items must be excluded by the user's goal."});
    }
    private CommandResponse SellCropStack(GameCommand c)
    {
        var q=RequireCropQuote(c); int keep=ShopInt(c,"keep_quantity"),minimum=ShopInt(c,"minimum_total_price");
        if(keep<0 || minimum<1 || q.Chest!=null) throw new InvalidOperationException("Invalid sale limits");
        string key=CropReceiptKey(q,"sell",keep,minimum);
        if(_cropReceipts.TryGetValue(key,out var cached)) return FarmReply(c,cached);
        if(Game1.currentLocation.Name!="SeedShop" || Game1.activeClickableMenu is not ShopMenu shop || !ReadPierreStatus().MenuOpen) throw new InvalidOperationException("Requires Pierre's actual open shop menu");
        if(ShopNumber(shop,"currency")!=0 || !ShopHasMember(shop,"heldItem") || ShopMember(shop,"heldItem")!=null) throw new InvalidOperationException("Gold shop and empty cursor required");
        if(q.Slot>=Game1.player.Items.Count || !ReferenceEquals(Game1.player.Items[q.Slot],q.Item) || q.Item.Stack!=q.Count || TradeQuality(q.Item)!=q.Quality || !TradeCrop(q.Item)) throw new InvalidOperationException("STALE_INVENTORY_QUOTE");
        string id=q.Item.QualifiedItemId;
        if(CountCrop(Game1.player.Items,id)-q.Count<keep) throw new InvalidOperationException("KEEP_QUANTITY: whole stack would consume reserved crops");
        var inventory=ShopMember(shop,"inventory")??throw new InvalidOperationException("Missing inventory UI");
        if(ShopMember(inventory,"highlightMethod") is not Delegate accepts || accepts.DynamicInvoke(q.Item) is not bool accepted || !accepted) throw new InvalidOperationException("SHOP_DOES_NOT_ACCEPT_ITEM_OR_UNKNOWN");
        // Use the menu's sale multiplier if available. Unknown modifiers disable the minimum-price check before any click.
        object? multiplier=ShopMember(shop,"sellPercentage");
        if(multiplier is not float && multiplier is not double && multiplier is not decimal) throw new InvalidOperationException("UNKNOWN_SHOP_SELL_MULTIPLIER");
        long estimate=(long)Math.Floor(((StardewValley.Object)q.Item).sellToStorePrice()*Convert.ToDouble(multiplier))*q.Count;
        if(estimate<minimum || estimate+Game1.player.Money>int.MaxValue) throw new InvalidOperationException("MINIMUM_PRICE_OR_MONEY_LIMIT");
        var slots=ShopList(inventory,"inventory");if(q.Slot>=slots.Count) throw new InvalidOperationException("Missing inventory slot");
        var bounds=ShopBounds(slots[q.Slot]!);NoTradeModifiers();
        int beforeMoney=Game1.player.Money,before=CountCrop(Game1.player.Items,id,q.Quality);
        object result=new {status="FAILED",reason="SALE_OUTCOME_UNKNOWN",itemId=id};_cropReceipts[key]=result;
        try {
            shop.receiveLeftClick(bounds.Center.X,bounds.Center.Y);
            int removed=before-CountCrop(Game1.player.Items,id,q.Quality),earned=Game1.player.Money-beforeMoney;
            bool verified=removed==q.Count && earned>=minimum && ShopMember(shop,"heldItem")==null;
            result=new {status=verified?"COMPLETED":"BLOCKED",reason=verified?"SALE_VERIFIED":"SALE_DELTA_MISMATCH; no automatic retry",itemId=id,quality=q.Quality,requested=q.Count,removed,earned,moneyAfter=Game1.player.Money,heldItem=ShopMember(shop,"heldItem") is Item held?held.QualifiedItemId:null};
        } catch(Exception ex) {result=new {status="BLOCKED",reason=ex.Message,itemId=id,removed=before-CountCrop(Game1.player.Items,id,q.Quality),earned=Game1.player.Money-beforeMoney,requiresInspection=true};}
        _cropReceipts[key]=result;_monitor.Log("[CROP SALE] "+System.Text.Json.JsonSerializer.Serialize(result),LogLevel.Info);return FarmReply(c,result);
    }
    private static Chest? StorageSource(IClickableMenu? menu)
        => menu is ItemGrabMenu ? (ShopMember(menu,"sourceItem") as Chest ?? ShopMember(menu,"context") as Chest) : null;
    private CommandResponse InspectStorage(GameCommand c)
    {
        var chests=new List<object>();_chestObservations.Clear();
        foreach(var entry in Game1.currentLocation.Objects.Pairs) if(entry.Value is Chest chest) {
            int x=(int)entry.Key.X,y=(int)entry.Key.Y;
            if(Math.Abs(x-Game1.player.Tile.X)+Math.Abs(y-Game1.player.Tile.Y)>12) continue;
            // Restrict to regular player-owned chests; never use shipping bins, Junimo/shared inventories, fridges, or machines.
            if(!ShopBool(ShopMember(chest,"playerChest")) || ShopBool(ShopMember(chest,"fridge"))) continue;
            object? special=ShopMember(chest,"SpecialChestType");if(special==null || special.ToString()!="None") continue;
            string token=Guid.NewGuid().ToString("N");_chestObservations[token]=(Game1.currentLocation.Name,new Point(x,y),chest);
            chests.Add(new {chestId=token,x,y,name=chest.DisplayName});
        }
        var rows=new List<object>();var menu=Game1.activeClickableMenu;var source=StorageSource(menu);
        bool supported=source!=null && _chestObservations.Values.Any(v=>ReferenceEquals(v.Chest,source));
        if(supported) {
            var top=ShopMember(menu!,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest inventory UI");
            var items=TradeItems(top);
            for(int i=0;i<items.Count;i++) if(TradeCrop(items[i])) rows.Add(CropRow(QuoteCrop(items[i]!,i,source,menu)));
        }
        return FarmReply(c,new {status="OBSERVED",location=Game1.currentLocation.Name,chests,storageOpen=supported,crops=rows,note="Contents are read only from an opened, nearby regular chest. No remote withdrawal."});
    }
    private CommandResponse OpenStorage(GameCommand c)
    {
        if(!_chestObservations.TryGetValue(ShopText(c,"chest_id"),out var storage) || storage.Location!=Game1.currentLocation.Name) throw new InvalidOperationException("Inspect nearby chests first");
        if(!Game1.currentLocation.Objects.TryGetValue(new Vector2(storage.Tile.X,storage.Tile.Y),out var obj) || !ReferenceEquals(obj,storage.Chest)) throw new InvalidOperationException("CHEST_CHANGED");
        int dx=storage.Tile.X-(int)Game1.player.Tile.X,dy=storage.Tile.Y-(int)Game1.player.Tile.Y;
        if(Math.Abs(dx)+Math.Abs(dy)!=1 || Game1.activeClickableMenu!=null || !Game1.player.CanMove || Game1.player.UsingTool) throw new InvalidOperationException("Approach the observed chest cardinally first");
        ClearMovementState();Game1.player.faceDirection(dx==1?1:dx==-1?3:dy==1?2:0);
        Game1.currentCursorTile=new Vector2(storage.Tile.X,storage.Tile.Y);Game1.lastCursorMotionWasMouse=false;
        Game1.setMousePosition(storage.Tile.X*64+32-Game1.viewport.X,storage.Tile.Y*64+32-Game1.viewport.Y);
        _helper.Input.Press(Game1.options.actionButton.Length>0?Game1.options.actionButton[0].ToSButton():SButton.MouseRight);
        return FarmReply(c,new {status="INPUT_SENT",note="Call inspect_storage to verify opening before any withdrawal"});
    }
    private CommandResponse TakeStorageCrop(GameCommand c)
    {
        var q=RequireCropQuote(c);int keep=ShopInt(c,"keep_in_chest");if(keep<0 || q.Chest==null) throw new InvalidOperationException("Invalid storage quote/keep quantity");
        string key=CropReceiptKey(q,"take",keep);if(_cropReceipts.TryGetValue(key,out var cached))return FarmReply(c,cached);
        var menu=Game1.activeClickableMenu;
        if(menu is not ItemGrabMenu || !ReferenceEquals(menu,q.Menu) || !ReferenceEquals(StorageSource(menu),q.Chest) || q.Location!=Game1.currentLocation.Name) throw new InvalidOperationException("Open the quoted chest again and inspect it");
        if(!ShopHasMember(menu,"heldItem") || ShopMember(menu,"heldItem")!=null) throw new InvalidOperationException("Empty cursor required");
        var top=ShopMember(menu,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest UI");var items=TradeItems(top);
        if(q.Slot>=items.Count || !ReferenceEquals(items[q.Slot],q.Item) || q.Item.Stack!=q.Count || TradeQuality(q.Item)!=q.Quality || !TradeCrop(q.Item)) throw new InvalidOperationException("STALE_CHEST_QUOTE");
        string id=q.Item.QualifiedItemId;if(CountCrop(items,id)-q.Count<keep) throw new InvalidOperationException("KEEP_IN_CHEST: whole stack would consume reserved crops");
        int empty=-1;for(int i=0;i<Game1.player.Items.Count;i++)if(Game1.player.Items[i]==null){empty=i;break;}
        if(empty<0)throw new InvalidOperationException("One empty inventory slot required");
        var slots=ShopList(top,"inventory");var inventory=ShopMember(menu,"inventory")??throw new InvalidOperationException("Missing player inventory UI");var playerSlots=ShopList(inventory,"inventory");
        if(q.Slot>=slots.Count || empty>=playerSlots.Count)throw new InvalidOperationException("Slot unavailable");
        var take=ShopBounds(slots[q.Slot]!);var deposit=ShopBounds(playerSlots[empty]!);NoTradeModifiers();
        int beforePlayer=CountCrop(Game1.player.Items,id,q.Quality),beforeChest=CountCrop(items,id,q.Quality);
        object result=new {status="FAILED",reason="TRANSFER_OUTCOME_UNKNOWN"};_cropReceipts[key]=result;
        try {
            menu.receiveLeftClick(take.Center.X,take.Center.Y);
            if(ShopMember(menu,"heldItem") is Item held) {
                if(held.QualifiedItemId!=id || TradeQuality(held)!=q.Quality || held.Stack!=q.Count || Game1.player.Items[empty]!=null)throw new InvalidOperationException("CURSOR_OR_DESTINATION_CHANGED");
                menu.receiveLeftClick(deposit.Center.X,deposit.Center.Y);
            }
            int received=CountCrop(Game1.player.Items,id,q.Quality)-beforePlayer,removed=beforeChest-CountCrop(TradeItems(top),id,q.Quality);
            bool verified=received==q.Count && removed==q.Count && ShopMember(menu,"heldItem")==null;
            result=new {status=verified?"COMPLETED":"BLOCKED",reason=verified?"TRANSFER_VERIFIED":"TRANSFER_DELTA_MISMATCH; no retry",itemId=id,quality=q.Quality,received,removed};
        }catch(Exception ex){result=new {status="BLOCKED",reason=ex.Message,itemId=id,received=CountCrop(Game1.player.Items,id,q.Quality)-beforePlayer,removed=beforeChest-CountCrop(TradeItems(top),id,q.Quality),requiresInspection=true};}
        _cropReceipts[key]=result;_monitor.Log("[CROP TRANSFER] "+System.Text.Json.JsonSerializer.Serialize(result),LogLevel.Info);return FarmReply(c,result);
    }
    private CommandResponse CloseStorage(GameCommand c)
    {
        var menu=Game1.activeClickableMenu;if(StorageSource(menu)==null || menu==null)throw new InvalidOperationException("No chest menu open");
        if(!ShopHasMember(menu,"heldItem") || ShopMember(menu,"heldItem")!=null || !menu.readyToClose()) throw new InvalidOperationException("Place cursor item in inventory before closing");
        menu.exitThisMenu();return FarmReply(c,new {status=Game1.activeClickableMenu==null?"COMPLETED":"BLOCKED"});
    }
}
