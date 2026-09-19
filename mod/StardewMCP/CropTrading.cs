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
    private static bool SafeStorageItem(Item? item) => item is StardewValley.Object && !ShopBool(ShopMember(item,"questItem")) && !ShopBool(ShopMember(item,"specialItem")) && !ShopBool(ShopMember(item,"bigCraftable"));
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
    private static List<Item?> StorageMenuItems(object inventory)
    {
        var items=TradeItems(inventory);
        int capacity=ShopList(inventory,"inventory").Count;
        if(items.Count>capacity) throw new InvalidOperationException("Storage item list exceeds visible capacity");
        while(items.Count<capacity) items.Add(null);
        return items;
    }
    private static int CountCrop(IEnumerable<Item?> items,string id,int? quality=null) => items.Where(i=>i!=null && i.QualifiedItemId==id && (!quality.HasValue || TradeQuality(i)==quality)).Sum(i=>i!.Stack);
    private CropQuote QuoteCrop(Item item,int slot,Chest? chest=null,IClickableMenu? menu=null)
    {
        if(_cropQuotes.Count>=512) _cropQuotes.Clear();
        var q=new CropQuote {Id=Guid.NewGuid().ToString("N"),Location=Game1.currentLocation.Name,Slot=slot,Count=item.Stack,Quality=TradeQuality(item),Item=item,Chest=chest,Menu=menu};
        _cropQuotes[q.Id]=q;return q;
    }
    private object CropRow(CropQuote q) => new {quoteId=q.Id,slot=q.Slot,itemId=q.Item.QualifiedItemId,name=q.Item.DisplayName,quantity=q.Count,quality=q.Quality,estimatedUnitPrice=((StardewValley.Object)q.Item).sellToStorePrice(),wholeStackOnly=true};
    private object StorageRow(Item item,int slot,string source,string? quoteId) => new {quoteId,transferable=quoteId!=null,source,slot,itemId=item.QualifiedItemId,name=item.DisplayName,quantity=item.Stack,quality=TradeQuality(item),category=item.Category,type=item.GetType().Name,wholeStackOnly=true};
    private object ReadOnlyStorageRow(Item item,int slot) => new {slot,itemId=item.QualifiedItemId,name=item.DisplayName,quantity=item.Stack,quality=TradeQuality(item),category=item.Category,type=item.GetType().Name};
    private object StorageSummary(List<Item?> items) {var present=items.Where(i=>i!=null).Select(i=>i!).ToList();return new {totalItemUnits=present.Sum(i=>i.Stack),occupiedSlots=present.Count,freeSlots=items.Count-present.Count,capacity=items.Count,uniqueItemIds=present.Select(i=>i.QualifiedItemId).Distinct().Count(),itemTotals=present.GroupBy(i=>new{i.QualifiedItemId,i.DisplayName}).Select(g=>new{itemId=g.Key.QualifiedItemId,name=g.Key.DisplayName,totalQuantity=g.Sum(i=>i.Stack),stackCount=g.Count(),qualityTotals=g.GroupBy(TradeQuality).Select(q=>new{quality=q.Key,quantity=q.Sum(i=>i.Stack),stackCount=q.Count()}).ToList()}).OrderBy(r=>r.name).ToList()};}
    private static int ClosedStorageCapacity(Chest chest)
    {
        var method=chest.GetType().GetMethod("GetActualCapacity",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,Type.DefaultBinder,Type.EmptyTypes,null);
        if(method?.Invoke(chest,null) is int capacity && capacity>=chest.Items.Count) return capacity;
        throw new InvalidOperationException("Unable to read exact chest capacity");
    }
    private static object ClosedStorageColor(Chest chest)
    {
        object? holder=ShopMember(chest,"playerChoiceColor");object? raw=holder==null?null:ShopMember(holder,"Value")??holder;
        return raw is Color color?new {r=(int)color.R,g=(int)color.G,b=(int)color.B,a=(int)color.A}:new {r=-1,g=-1,b=-1,a=-1};
    }
    private static bool StorageCanStack(Item moving,Item? destination) {if(destination==null || moving.QualifiedItemId!=destination.QualifiedItemId || TradeQuality(moving)!=TradeQuality(destination))return false;foreach(var method in moving.GetType().GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)){if(!method.Name.Equals("canStackWith",StringComparison.OrdinalIgnoreCase)||method.GetParameters().Length!=1)continue;try{if(method.Invoke(moving,new object?[]{destination}) is bool compatible)return compatible;}catch(Exception){}}return false;}
    private static List<int> StorageDestinationSlots(List<Item?> items,Item moving) {var result=new List<int>();int remaining=moving.Stack;for(int i=0;i<items.Count;i++)if(StorageCanStack(moving,items[i])){int capacity=Math.Max(0,items[i]!.maximumStackSize()-items[i]!.Stack);if(capacity==0)continue;result.Add(i);remaining-=capacity;if(remaining<=0)return result;}for(int i=0;i<items.Count;i++)if(items[i]==null){result.Add(i);return result;}return new List<int>();}
    private static int StorageTotalUnits(object inventory) => StorageMenuItems(inventory).Where(i=>i!=null).Sum(i=>i!.Stack);
    private (int Before,int After) OrganizeStorageMenu(ItemGrabMenu menu,object top)
    {
        if(ShopMember(menu,"heldItem")!=null) throw new InvalidOperationException("Empty cursor required before organizing storage");
        var button=ShopMember(menu,"organizeButton")??throw new InvalidOperationException("This chest menu does not expose the Organize button");
        int before=StorageTotalUnits(top);var bounds=ShopBounds(button);NoTradeModifiers();
        menu.receiveLeftClick(bounds.Center.X,bounds.Center.Y);
        int after=StorageTotalUnits(top);
        _cropQuotes.Clear();
        if(before!=after || ShopMember(menu,"heldItem")!=null) throw new InvalidOperationException($"ORGANIZE_TOTAL_MISMATCH: before={before}, after={after}; no retry");
        return (before,after);
    }
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
            if(!IsRegularPlayerChest(chest)) continue;
            string token=Guid.NewGuid().ToString("N");_chestObservations[token]=(Game1.currentLocation.Name,new Point(x,y),chest);
            string memoryId=RememberChest(Game1.currentLocation.Name,x,y,chest,true);
            chests.Add(new {chestId=token,memoryId,x,y,name=chest.DisplayName});
        }
        var rows=new List<object>();var inventoryRows=new List<object>();var menu=Game1.activeClickableMenu;var source=StorageSource(menu);object? chestSummary=null,inventorySummary=null;
        bool supported=source!=null && _chestObservations.Values.Any(v=>ReferenceEquals(v.Chest,source));
        if(supported) {
            var top=ShopMember(menu!,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest inventory UI");
            var items=StorageMenuItems(top);
            for(int i=0;i<items.Count;i++) if(items[i]!=null){var item=items[i]!;string? quote=SafeStorageItem(item)?QuoteCrop(item,i,source,menu).Id:null;rows.Add(StorageRow(item,i,"chest",quote));}
            var playerItems=Game1.player.Items.ToList();for(int i=0;i<playerItems.Count;i++)if(playerItems[i]!=null){var item=playerItems[i]!;string? quote=SafeStorageItem(item)?QuoteCrop(item,i,null,menu).Id:null;inventoryRows.Add(StorageRow(item,i,"inventory",quote));}
            chestSummary=StorageSummary(items);inventorySummary=StorageSummary(playerItems);
        }
        FlushLongTermMemory();
        return FarmReply(c,new {status="OBSERVED",location=Game1.currentLocation.Name,chests,storageOpen=supported,chestSummary,inventorySummary,items=rows,inventory=inventoryRows,note="Counts include every visible stack. Only transferable rows have quoteId. Compatible partial stacks are used before empty slots."});
    }
    private CommandResponse InspectClosedStorage(GameCommand c)
    {
        var chests=new List<object>();
        foreach(var entry in Game1.currentLocation.Objects.Pairs) if(entry.Value is Chest chest) {
            if(!IsRegularPlayerChest(chest)) continue;
            int capacity=ClosedStorageCapacity(chest);var items=chest.Items.Select(i=>(Item?)i).ToList();
            while(items.Count<capacity) items.Add(null);
            var rows=new List<object>();for(int i=0;i<items.Count;i++)if(items[i]!=null)rows.Add(ReadOnlyStorageRow(items[i]!,i));
            string memoryId=RememberChest(Game1.currentLocation.Name,(int)entry.Key.X,(int)entry.Key.Y,chest,true);
            chests.Add(new {memoryId,x=(int)entry.Key.X,y=(int)entry.Key.Y,color=ClosedStorageColor(chest),summary=StorageSummary(items),items=rows});
        }
        FlushLongTermMemory();
        return FarmReply(c,new {status="OBSERVED_READ_ONLY",location=Game1.currentLocation.Name,chests,note="Read-only closed-chest contents. No quote IDs or mutation handles are returned. Open the chest menu normally before any transfer."});
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
    private CommandResponse TakeStorageItem(GameCommand c)
    {
        var q=RequireCropQuote(c);int keep=ShopInt(c,"keep_in_chest");if(keep<0 || q.Chest==null) throw new InvalidOperationException("Invalid storage quote/keep quantity");
        string key=CropReceiptKey(q,"take",keep);if(_cropReceipts.TryGetValue(key,out var cached))return FarmReply(c,cached);
        var menu=Game1.activeClickableMenu;
        if(menu is not ItemGrabMenu || !ReferenceEquals(menu,q.Menu) || !ReferenceEquals(StorageSource(menu),q.Chest) || q.Location!=Game1.currentLocation.Name) throw new InvalidOperationException("Open the quoted chest again and inspect it");
        if(!ShopHasMember(menu,"heldItem") || ShopMember(menu,"heldItem")!=null) throw new InvalidOperationException("Empty cursor required");
        var top=ShopMember(menu,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest UI");var items=StorageMenuItems(top);
        if(q.Slot>=items.Count || !ReferenceEquals(items[q.Slot],q.Item) || q.Item.Stack!=q.Count || TradeQuality(q.Item)!=q.Quality || !SafeStorageItem(q.Item)) throw new InvalidOperationException("STALE_CHEST_QUOTE");
        string id=q.Item.QualifiedItemId;if(CountCrop(items,id)-q.Count<keep) throw new InvalidOperationException("KEEP_IN_CHEST: whole stack would consume reserved crops");
        var destinations=StorageDestinationSlots(Game1.player.Items.ToList(),q.Item);if(destinations.Count==0)throw new InvalidOperationException("No compatible inventory stack capacity or empty slot");
        var slots=ShopList(top,"inventory");var inventory=ShopMember(menu,"inventory")??throw new InvalidOperationException("Missing player inventory UI");var playerSlots=ShopList(inventory,"inventory");
        if(q.Slot>=slots.Count || destinations.Any(d=>d>=playerSlots.Count))throw new InvalidOperationException("Slot unavailable");
        var take=ShopBounds(slots[q.Slot]!);NoTradeModifiers();
        int beforePlayer=CountCrop(Game1.player.Items,id,q.Quality),beforeChest=CountCrop(items,id,q.Quality);
        object result=new {status="FAILED",reason="TRANSFER_OUTCOME_UNKNOWN"};_cropReceipts[key]=result;
        try {
            menu.receiveLeftClick(take.Center.X,take.Center.Y);
            foreach(int destination in destinations)if(ShopMember(menu,"heldItem") is Item held){if(held.QualifiedItemId!=id||TradeQuality(held)!=q.Quality)throw new InvalidOperationException("CURSOR_OR_DESTINATION_CHANGED");var deposit=ShopBounds(playerSlots[destination]!);menu.receiveLeftClick(deposit.Center.X,deposit.Center.Y);}
            int received=CountCrop(Game1.player.Items,id,q.Quality)-beforePlayer,removed=beforeChest-CountCrop(StorageMenuItems(top),id,q.Quality);
            bool verified=received==q.Count && removed==q.Count && ShopMember(menu,"heldItem")==null;
            if(verified) {var organized=OrganizeStorageMenu((ItemGrabMenu)menu,top);result=new {status="COMPLETED",reason="TRANSFER_AND_ORGANIZE_VERIFIED",itemId=id,quality=q.Quality,received,removed,organizedBefore=organized.Before,organizedAfter=organized.After};}
            else result=new {status="BLOCKED",reason="TRANSFER_DELTA_MISMATCH; no retry",itemId=id,quality=q.Quality,received,removed,organizedBefore=-1,organizedAfter=-1};
        }catch(Exception ex){result=new {status="BLOCKED",reason=ex.Message,itemId=id,received=CountCrop(Game1.player.Items,id,q.Quality)-beforePlayer,removed=beforeChest-CountCrop(StorageMenuItems(top),id,q.Quality),requiresInspection=true};}
        _cropReceipts[key]=result;RefreshChestMemory(q.Chest);_monitor.Log("[STORAGE TAKE] "+System.Text.Json.JsonSerializer.Serialize(result),LogLevel.Info);return FarmReply(c,result);
    }
    private CommandResponse StoreInventoryItem(GameCommand c)
    {
        var q=RequireCropQuote(c);int keep=ShopInt(c,"keep_in_inventory");if(keep<0||q.Chest!=null)throw new InvalidOperationException("Invalid inventory quote/keep quantity");string key=CropReceiptKey(q,"store",keep);if(_cropReceipts.TryGetValue(key,out var cached))return FarmReply(c,cached);
        var menu=Game1.activeClickableMenu;var chest=StorageSource(menu);if(menu is not ItemGrabMenu||chest==null||!ReferenceEquals(menu,q.Menu)||q.Location!=Game1.currentLocation.Name)throw new InvalidOperationException("Open the quoted chest again and inspect it");if(!ShopHasMember(menu,"heldItem")||ShopMember(menu,"heldItem")!=null)throw new InvalidOperationException("Empty cursor required");
        if(q.Slot>=Game1.player.Items.Count||!ReferenceEquals(Game1.player.Items[q.Slot],q.Item)||q.Item.Stack!=q.Count||TradeQuality(q.Item)!=q.Quality||!SafeStorageItem(q.Item))throw new InvalidOperationException("STALE_INVENTORY_QUOTE");string id=q.Item.QualifiedItemId;if(CountCrop(Game1.player.Items,id)-q.Count<keep)throw new InvalidOperationException("KEEP_IN_INVENTORY");
        var top=ShopMember(menu,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest inventory UI");var items=StorageMenuItems(top);if(StorageDestinationSlots(items,q.Item).Count==0)throw new InvalidOperationException("No compatible chest stack capacity or empty slot");NoTradeModifiers();
        int beforePlayer=CountCrop(Game1.player.Items,id,q.Quality),beforeChest=CountCrop(items,id,q.Quality),beforeCombinedUnits=Game1.player.Items.Where(i=>i!=null).Sum(i=>i.Stack)+StorageTotalUnits(top);
        object result=new{status="FAILED",reason="TRANSFER_OUTCOME_UNKNOWN"};_cropReceipts[key]=result;
        try {
            Item moving=q.Item.getOne();moving.Stack=q.Count;
            Item? leftover=chest.addItem(moving);
            int accepted=q.Count-(leftover?.Stack??0);
            if(accepted<0 || accepted>q.Count) throw new InvalidOperationException("CHEST_ACCEPTED_INVALID_QUANTITY; no retry");
            if(accepted>0) {
                q.Item.Stack-=accepted;
                if(q.Item.Stack==0) Game1.player.Items[q.Slot]=null;
            }
            int removed=beforePlayer-CountCrop(Game1.player.Items,id,q.Quality),stored=CountCrop(StorageMenuItems(top),id,q.Quality)-beforeChest;
            int afterCombinedUnits=Game1.player.Items.Where(i=>i!=null).Sum(i=>i.Stack)+StorageTotalUnits(top);
            bool conserved=removed==stored && removed==accepted && beforeCombinedUnits==afterCombinedUnits && ShopMember(menu,"heldItem")==null;
            if(!conserved) result=new {status="BLOCKED",reason="CONSERVATION_CHECK_FAILED; no retry",itemId=id,quality=q.Quality,requested=q.Count,accepted,removed,stored,beforeCombinedUnits,afterCombinedUnits,requiresInspection=true};
            else {
                var organized=OrganizeStorageMenu((ItemGrabMenu)menu,top);
                result=new {status=accepted==q.Count?"COMPLETED":"BLOCKED",reason=accepted==q.Count?"NATIVE_CHEST_ADD_AND_ORGANIZE_VERIFIED":"PARTIAL_ACCEPT_PRESERVED; inspect before retry",itemId=id,quality=q.Quality,requested=q.Count,accepted,removed,stored,remainingInInventory=q.Count-accepted,beforeCombinedUnits,afterCombinedUnits,organizedBefore=organized.Before,organizedAfter=organized.After};
            }
        }catch(Exception ex){result=new{status="BLOCKED",reason=ex.Message,itemId=id,removed=beforePlayer-CountCrop(Game1.player.Items,id,q.Quality),stored=CountCrop(StorageMenuItems(top),id,q.Quality)-beforeChest,requiresInspection=true};}
        _cropReceipts[key]=result;RefreshChestMemory(chest);_monitor.Log("[STORAGE PUT] "+System.Text.Json.JsonSerializer.Serialize(result),LogLevel.Info);return FarmReply(c,result);
    }
    private CommandResponse StackInventoryToStorage(GameCommand c)
    {
        var menu=Game1.activeClickableMenu;
        if(menu is not ItemGrabMenu || StorageSource(menu)==null) throw new InvalidOperationException("Open a regular chest first");
        if(!ShopHasMember(menu,"heldItem") || ShopMember(menu,"heldItem")!=null) throw new InvalidOperationException("Empty cursor required");
        var button=ShopMember(menu,"fillStacksButton")??throw new InvalidOperationException("This chest menu does not expose the Add To Existing Stacks button");
        var top=ShopMember(menu,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest inventory UI");
        int beforeInventory=Game1.player.Items.Where(i=>i!=null).Sum(i=>i.Stack);
        int beforeChest=StorageMenuItems(top).Where(i=>i!=null).Sum(i=>i!.Stack);
        var bounds=ShopBounds(button);NoTradeModifiers();
        menu.receiveLeftClick(bounds.Center.X,bounds.Center.Y);
        int afterInventory=Game1.player.Items.Where(i=>i!=null).Sum(i=>i.Stack);
        int afterChest=StorageMenuItems(top).Where(i=>i!=null).Sum(i=>i!.Stack);
        int removed=beforeInventory-afterInventory,stored=afterChest-beforeChest;
        bool verified=removed>=0 && removed==stored && beforeInventory+beforeChest==afterInventory+afterChest && ShopMember(menu,"heldItem")==null;
        object result;
        if(verified) {var organized=OrganizeStorageMenu((ItemGrabMenu)menu,top);result=new {status="COMPLETED",reason=stored>0?"ADD_TO_EXISTING_STACKS_AND_ORGANIZE_VERIFIED":"NO_MATCHING_STACKS; ORGANIZE_VERIFIED",removedFromInventory=removed,storedInChest=stored,organizedBefore=organized.Before,organizedAfter=organized.After};}
        else result=new {status="BLOCKED",reason="TRANSFER_DELTA_MISMATCH; no retry",removedFromInventory=removed,storedInChest=stored,organizedBefore=-1,organizedAfter=-1};
        RefreshChestMemory(StorageSource(menu)!);_monitor.Log("[STORAGE STACK EXISTING] "+System.Text.Json.JsonSerializer.Serialize(result),LogLevel.Info);return FarmReply(c,result);
    }
    private CommandResponse OrganizeStorage(GameCommand c)
    {
        var menu=Game1.activeClickableMenu;
        if(menu is not ItemGrabMenu || StorageSource(menu)==null) throw new InvalidOperationException("Open a regular chest first");
        var top=ShopMember(menu,"ItemsToGrabMenu")??throw new InvalidOperationException("Missing chest inventory UI");
        var organized=OrganizeStorageMenu((ItemGrabMenu)menu,top);
        RefreshChestMemory(StorageSource(menu)!);
        return FarmReply(c,new {status="COMPLETED",reason="ORGANIZE_TOTAL_VERIFIED",totalUnitsBefore=organized.Before,totalUnitsAfter=organized.After});
    }
    private CommandResponse CloseStorage(GameCommand c)
    {
        var menu=Game1.activeClickableMenu;if(StorageSource(menu)==null || menu==null)throw new InvalidOperationException("No chest menu open");
        if(!ShopHasMember(menu,"heldItem") || ShopMember(menu,"heldItem")!=null || !menu.readyToClose()) throw new InvalidOperationException("Place cursor item in inventory before closing");
        Game1.exitActiveMenu();
        bool closed=Game1.activeClickableMenu==null || !ReferenceEquals(Game1.activeClickableMenu,menu);
        return FarmReply(c,new {status=closed?"COMPLETED":"BLOCKED",reason=closed?"STORAGE_MENU_CLOSED":"MENU_CLOSE_NOT_CONFIRMED"});
    }
}
