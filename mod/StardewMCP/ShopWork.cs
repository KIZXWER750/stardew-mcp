using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewModdingAPI;
using StardewValley.Menus;

namespace StardewMCP;

public partial class CommandExecutor
{
    private sealed class ShopExit {
        public string Id="",From="",To="",Action="";
        public int X,Y;
        public List<Point> Approaches=new();
    }
    private readonly Dictionary<string,ShopExit> _shopExits=new();
    private ShopExit? _activeShopExit;
    private GameCommand? _shopExitCommand;
    private DateTime _shopExitStarted;
    private int _shopExitDirection;
    private bool _shopExitPressed;
    private ShopMenu? _observedShop;
    private string _shopObservation = "";
    private readonly Dictionary<string,(string Fingerprint,object Result)> _shopReceipts = new();
    private static object? ShopMember(object o,string name)
    {
        for(Type? t=o.GetType();t!=null;t=t.BaseType) {
            var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
            var f=t.GetField(name,flags); if(f!=null) return f.GetValue(o);
            var p=t.GetProperty(name,flags); if(p!=null) return p.GetValue(o);
        }
        return null;
    }
    private static bool ShopHasMember(object o,string name)
    {
        for(Type? t=o.GetType();t!=null;t=t.BaseType) {
            var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
            if(t.GetField(name,flags)!=null || t.GetProperty(name,flags)!=null) return true;
        }
        return false;
    }
    private static string? ShopTradeItem(object stock)
    {
        // Names differ between game API revisions. An unrecognized shape is not a gold quote.
        if(ShopMember(stock,"TradeItemId") is string id) return id;
        if(ShopMember(stock,"TradeItem") is string legacy) return legacy;
        var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
        if(new[]{"TradeItemId","TradeItem"}.Any(n=>stock.GetType().GetField(n,flags)!=null || stock.GetType().GetProperty(n,flags)!=null)) return null;
        throw new InvalidOperationException("Unsupported shop trade metadata; purchase disabled");
    }
    private static int ShopNumber(object o,string name) => ShopMember(o,name) is int n?n:throw new InvalidOperationException("Unsupported shop member: "+name);
    private static Rectangle ShopBounds(object o) => ShopMember(o,"bounds") is Rectangle r?r:throw new InvalidOperationException("Missing shop button bounds");
    private static IList ShopList(object o,string name) => ShopMember(o,name) as IList ?? throw new InvalidOperationException("Unsupported shop list: "+name);
    private static string ShopText(GameCommand c,string key) => c.Params.TryGetValue(key,out var v)?v?.ToString()??"":"";
    private int ShopInt(GameCommand c,string key) => c.Params.TryGetValue(key,out var v)?GetIntParam(v):throw new InvalidOperationException("Missing "+key);
    private int ShopInventoryCount(string id) => Game1.player.Items.Where(i=>i!=null && i.QualifiedItemId==id).Sum(i=>i.Stack);
    private int ShopHeldCount(ShopMenu s,string id) => ShopMember(s,"heldItem") is Item i && i.QualifiedItemId==id?i.Stack:0;

    private CommandResponse InspectShop(GameCommand c)
    {
        if(Game1.activeClickableMenu is not ShopMenu s) return FarmReply(c,new {status="BLOCKED",reason="SHOP_NOT_OPEN",location=Game1.currentLocation.Name});
        _observedShop=s; _shopObservation=Guid.NewGuid().ToString("N");
        var rows=new List<object>();
        foreach(var sale in s.forSale) {
            if(sale is not Item item || item.Category!=StardewValley.Object.SeedsCategory) continue;
            var stock=s.itemPriceAndStock[sale];
            rows.Add(new {itemId=item.QualifiedItemId,name=item.DisplayName,price=stock.Price,stock=ShopMember(stock,"Stock"),tradeItem=ShopMember(stock,"TradeItemId")??ShopMember(stock,"TradeItem"),tradeItemCount=ShopMember(stock,"TradeItemCount")});
        }
        return FarmReply(c,new {status="OBSERVED",observationId=_shopObservation,location=Game1.currentLocation.Name,money=Game1.player.Money,currency=ShopMember(s,"currency"),heldItem=ShopMember(s,"heldItem") is Item held?held.QualifiedItemId:null,seeds=rows,schedule=ReadPierreStatus()});
    }
    private CommandResponse BuyShopItem(GameCommand c)
    {
        string request=ShopText(c,"request_id"),id=ShopText(c,"item_id"),observation=ShopText(c,"observation_id");
        int quantity=ShopInt(c,"quantity"),budget=ShopInt(c,"max_total_cost"),reserve=ShopInt(c,"reserve_money");
        string fingerprint=$"{id}|{quantity}|{budget}|{reserve}";
        if(request.Length<8 || request.Length>128 || quantity<1 || quantity>99 || budget<0 || reserve<0) throw new InvalidOperationException("Invalid purchase limits/request ID");
        if(_shopReceipts.TryGetValue(request,out var receipt)) {
            if(receipt.Fingerprint!=fingerprint) throw new InvalidOperationException("REQUEST_ID_CONFLICT");
            return FarmReply(c,receipt.Result);
        }
        if(Game1.activeClickableMenu is not ShopMenu s || !ReferenceEquals(s,_observedShop) || observation!=_shopObservation || observation=="") throw new InvalidOperationException("Inspect the current shop before purchase");
        if(!ShopHasMember(s,"heldItem")) throw new InvalidOperationException("Unsupported cursor metadata");
        if(ShopNumber(s,"currency")!=0 || ShopMember(s,"heldItem")!=null) throw new InvalidOperationException("Requires gold shop and empty cursor");
        var keyboard=Keyboard.GetState();
        if(new[]{Keys.LeftShift,Keys.RightShift,Keys.LeftControl,Keys.RightControl}.Any(keyboard.IsKeyDown)) throw new InvalidOperationException("Release Shift and Ctrl before purchase");
        var sale=s.forSale.FirstOrDefault(x=>x is Item i && i.QualifiedItemId==id && i.Category==StardewValley.Object.SeedsCategory);
        if(sale is not Item seed) throw new InvalidOperationException("Observed seed not for sale");
        var price=s.itemPriceAndStock[sale];
        if(price.Price<=0 || !string.IsNullOrEmpty(ShopTradeItem(price))) throw new InvalidOperationException("Only positive-price gold seed sales supported");
        long total=(long)price.Price*quantity;
        if(total>budget || total>Game1.player.Money-reserve) throw new InvalidOperationException("BUDGET_OR_RESERVE");
        if(ShopMember(price,"Stock") is not int stock || stock<quantity) throw new InvalidOperationException("INSUFFICIENT_OR_UNKNOWN_STOCK");
        int slot=-1; for(int i=0;i<Game1.player.Items.Count;i++) if(Game1.player.Items[i]==null){slot=i;break;}
        if(slot<0 || quantity>seed.maximumStackSize()) throw new InvalidOperationException("Requires one empty inventory slot and a single-stack quantity");
        var inventory=ShopMember(s,"inventory")??throw new InvalidOperationException("Missing inventory UI");
        var slots=ShopList(inventory,"inventory");
        if(slot>=slots.Count) throw new InvalidOperationException("Inventory slot unavailable");
        var deposit=ShopBounds(slots[slot]!);
        var buttons=ShopList(s,"forSaleButtons");
        if(buttons.Count==0) throw new InvalidOperationException("No sale buttons available");
        int unitPrice=price.Price;
        int beforeMoney=Game1.player.Money,beforeCount=ShopInventoryCount(id),purchased=0;
        object result=new {status="FAILED",reason="PURCHASE_OUTCOME_UNKNOWN",requestId=request};
        // Reserve request before the first side effect. Unknown outcomes cannot be retried under this ID.
        _shopReceipts[request]=(fingerprint,result); _shopObservation="";
        _monitor.Log($"[SHOP BUY] request={request}, item={id}, quantity={quantity}, unitPrice={unitPrice}, budget={budget}, reserve={reserve}",LogLevel.Info);
        try {
            for(int n=0;n<quantity;n++) {
                if(Game1.player.Money-unitPrice<reserve || (long)(beforeMoney-Game1.player.Money)+unitPrice>budget) throw new InvalidOperationException("BUDGET_CHANGED");
                if(!ReferenceEquals(Game1.activeClickableMenu,s)) throw new InvalidOperationException("SHOP_CHANGED");
                int index=s.forSale.IndexOf(sale),offset=ShopNumber(s,"currentItemIndex");
                // Scroll by the menu's own wheel handler; never mutate stock or money.
                for(int attempt=0;index>=0 && (index<offset || index>=offset+buttons.Count) && attempt<200;attempt++) {
                    s.receiveScrollWheelAction(index<offset?120:-120); offset=ShopNumber(s,"currentItemIndex");
                }
                if(index<0 || index<offset || index>=offset+buttons.Count) throw new InvalidOperationException("SALE_ROW_NOT_VISIBLE");
                var bounds=ShopBounds(buttons[index-offset]!);
                if(!s.itemPriceAndStock.TryGetValue(sale,out var currentPrice) || currentPrice.Price!=unitPrice || !string.IsNullOrEmpty(ShopTradeItem(currentPrice))) throw new InvalidOperationException("QUOTE_CHANGED");
                int oldMoney=Game1.player.Money,oldCount=ShopInventoryCount(id)+ShopHeldCount(s,id);
                s.receiveLeftClick(bounds.Center.X,bounds.Center.Y);
                int added=ShopInventoryCount(id)+ShopHeldCount(s,id)-oldCount;
                if(added!=1 || oldMoney-Game1.player.Money!=unitPrice) throw new InvalidOperationException("PURCHASE_DELTA_MISMATCH; do not repeat");
                purchased++;
            }
            if(ShopMember(s,"heldItem") is Item held) {
                if(held.QualifiedItemId!=id || Game1.player.Items[slot]!=null) throw new InvalidOperationException("CURSOR_OR_SLOT_CHANGED");
                s.receiveLeftClick(deposit.Center.X,deposit.Center.Y);
            }
            if(ShopMember(s,"heldItem")!=null || ShopInventoryCount(id)-beforeCount!=quantity || beforeMoney-Game1.player.Money!=total) throw new InvalidOperationException("INVENTORY_VERIFICATION_FAILED");
            result=new {status="COMPLETED",requestId=request,itemId=id,quantity,received=quantity,spent=beforeMoney-Game1.player.Money,moneyAfter=Game1.player.Money};
        } catch(Exception ex) {
            result=new {status="BLOCKED",requestId=request,itemId=id,reason=ex.Message,confirmedClicks=purchased,received=ShopInventoryCount(id)-beforeCount,held=ShopHeldCount(s,id),spent=beforeMoney-Game1.player.Money,requiresInspection=true};
        }
        _shopReceipts[request]=(fingerprint,result);
        _monitor.Log("[SHOP RESULT] "+System.Text.Json.JsonSerializer.Serialize(result),LogLevel.Info);
        return FarmReply(c,result);
    }
    private CommandResponse CloseShop(GameCommand c)
    {
        if(Game1.activeClickableMenu is not ShopMenu s) return FarmReply(c,new {status="COMPLETED",closed=false});
        if(!ShopHasMember(s,"heldItem") || ShopMember(s,"heldItem")!=null || !s.readyToClose()) throw new InvalidOperationException("Cannot close: cursor item or menu busy; resolve manually");
        s.exitThisMenu(); _observedShop=null; _shopObservation="";
        return FarmReply(c,new {status=Game1.activeClickableMenu==null?"COMPLETED":"BLOCKED",closed=Game1.activeClickableMenu==null});
    }

    private CommandResponse FindShopRoute(GameCommand c)
    {
        string target=ShopText(c,"destination"); if(target=="") target="SeedShop";
        var edges=new List<(string From,string To,int X,int Y,string Action)>();
        var counters=new List<object>();
        foreach(var loc in Game1.locations) {
            foreach(var w in loc.warps) edges.Add((loc.Name,w.TargetName,w.X,w.Y,"walk onto warp"));
            foreach(var door in loc.doors.Pairs) edges.Add((loc.Name,door.Value,door.Key.X,door.Key.Y,"interact with door"));
            if(loc.Map==null || loc.Map.Layers.Count==0) continue;
            for(int y=0;y<loc.Map.Layers[0].LayerHeight;y++) for(int x=0;x<loc.Map.Layers[0].LayerWidth;x++) {
                string action=loc.doesTileHaveProperty(x,y,"Action","Buildings")??"";
                var parts=action.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length>=4 && (parts[0]=="Warp" || parts[0]=="LockedDoorWarp") && int.TryParse(parts[1],out _) && int.TryParse(parts[2],out _)) edges.Add((loc.Name,parts[3],x,y,action));
                if(loc==Game1.currentLocation && parts.Length>0 && (parts[0]=="Shop" || parts[0]=="SeedShop")) counters.Add(new {x,y,action});
            }
        }
        var queue=new Queue<string>(); var seen=new HashSet<string>{Game1.currentLocation.Name};
        var parents=new Dictionary<string,(string From,string To,int X,int Y,string Action)>(); queue.Enqueue(Game1.currentLocation.Name);
        while(queue.Count>0) {
            string from=queue.Dequeue(); if(from==target) break;
            foreach(var edge in edges.Where(e=>e.From==from)) if(seen.Add(edge.To)){parents[edge.To]=edge;queue.Enqueue(edge.To);}
        }
        var route=new List<object>(); string cursor=target;
        _shopExits.Clear();
        while(parents.TryGetValue(cursor,out var edge)) {
            var loc=Game1.locations.FirstOrDefault(l=>l.Name==edge.From);
            var exit=new ShopExit {Id=Guid.NewGuid().ToString("N"),From=edge.From,To=edge.To,X=edge.X,Y=edge.Y,Action=edge.Action};
            if(loc?.Map!=null && loc.Map.Layers.Count>0) {
                int width=loc.Map.Layers[0].LayerWidth,height=loc.Map.Layers[0].LayerHeight;
                foreach(var d in new[]{new Point(0,1),new Point(-1,0),new Point(1,0),new Point(0,-1)}) {
                    var a=new Point(edge.X+d.X,edge.Y+d.Y);
                    if(a.X>=0 && a.Y>=0 && a.X<width && a.Y<height) exit.Approaches.Add(a);
                }
            }
            _shopExits[exit.Id]=exit;
            route.Insert(0,new {exitId=exit.Id,from=edge.From,to=edge.To,x=edge.X,y=edge.Y,action=edge.Action,approaches=exit.Approaches.Select(a=>new {x=a.X,y=a.Y})});
            cursor=edge.From;
        }
        return FarmReply(c,new {status=!seen.Contains(target)?"BLOCKED":target=="SeedShop" && !ReadPierreStatus().CanAttemptTrade?"CLOSED_OR_UNKNOWN":"OBSERVED",location=Game1.currentLocation.Name,destination=target,route,counters,schedule=ReadPierreStatus(),accessVerified=false,note="Loaded-map links only. Verify each transition and opening hours in live gameplay. No movement performed."});
    }
    private CommandResponse UseShopExit(GameCommand c)
    {
        if(_activeShopExit!=null) throw new InvalidOperationException("Exit traversal already active");
        if(!_shopExits.TryGetValue(ShopText(c,"exit_id"),out var exit) || exit.From!=Game1.currentLocation.Name) throw new InvalidOperationException("Refresh route for the current location");
        if(exit.To=="SeedShop" && !ReadPierreStatus().CanAttemptTrade) throw new InvalidOperationException("SHOP_CLOSED: "+ReadPierreStatus().Reason);
        var tile=new Point((int)Game1.player.Tile.X,(int)Game1.player.Tile.Y);
        if(!exit.Approaches.Contains(tile) || Game1.activeClickableMenu!=null || Game1.player.UsingTool || !Game1.player.CanMove) throw new InvalidOperationException("Stand at a returned adjacent approach with no menu or tool action active");
        ClearMovementState();
        int dx=exit.X-tile.X,dy=exit.Y-tile.Y;
        _shopExitDirection=dx==1?1:dx==-1?3:dy==1?2:0;
        _activeShopExit=exit;_shopExitCommand=c;_shopExitStarted=DateTime.UtcNow;_shopExitPressed=false;
        return new CommandResponse {Id=c.Id,Success=true,Message="Exit traversal started"};
    }
    private void CancelShopExit(string reason)
    {
        var command=_shopExitCommand;_activeShopExit=null;_shopExitCommand=null;
        command?.OnComplete?.Invoke(new CommandResponse {Id=command.Id,Success=false,Message=reason});
    }
    private void UpdateShopExit()
    {
        var exit=_activeShopExit; var command=_shopExitCommand;
        if(exit==null || command==null) return;
        string location=Game1.currentLocation.Name;
        if(location!=exit.From) {
            _activeShopExit=null;_shopExitCommand=null;
            _monitor.Log($"[SHOP EXIT] from={exit.From}, expected={exit.To}, actual={location}",LogLevel.Info);
            command.OnComplete?.Invoke(FarmReply(command,new {status=location==exit.To?"COMPLETED":"BLOCKED",location,expectedLocation=exit.To,x=(int)Game1.player.Tile.X,y=(int)Game1.player.Tile.Y}));return;
        }
        if((DateTime.UtcNow-_shopExitStarted).TotalSeconds>5) {CancelShopExit($"EXIT_TIMEOUT at {location} ({Game1.player.Tile.X},{Game1.player.Tile.Y}); door may be closed or path blocked");return;}
        if(Game1.activeClickableMenu!=null) {CancelShopExit("Exit opened a dialogue/menu instead of changing location. Inspect manually before retrying.");return;}
        if(!Game1.player.CanMove || Game1.player.UsingTool) return;
        if(exit.Action=="walk onto warp") {
            var binding=_shopExitDirection==0?Game1.options.moveUpButton:_shopExitDirection==1?Game1.options.moveRightButton:_shopExitDirection==2?Game1.options.moveDownButton:Game1.options.moveLeftButton;
            var fallback=_shopExitDirection==0?SButton.W:_shopExitDirection==1?SButton.D:_shopExitDirection==2?SButton.S:SButton.A;
            _helper.Input.Press(binding.Length>0?binding[0].ToSButton():fallback);
        } else if(!_shopExitPressed) {
            Game1.player.faceDirection(_shopExitDirection);
            Game1.currentCursorTile=new Vector2(exit.X,exit.Y);
            Game1.lastCursorMotionWasMouse=false;
            Game1.setMousePosition(exit.X*64+32-Game1.viewport.X,exit.Y*64+32-Game1.viewport.Y);
            _helper.Input.Press(Game1.options.actionButton.Length>0?Game1.options.actionButton[0].ToSButton():SButton.MouseRight);
            _shopExitPressed=true;
        }
    }

}
