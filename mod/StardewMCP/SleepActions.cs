using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewMCP;

public partial class CommandExecutor
{
    private string? sleepStartDate;
    private bool sleepAnswered;
    private bool sleepMoveIssued;
    private Vector2? sleepApproach;
    private bool sleepEntered;
    private DateTime sleepStarted;

    private string DescribeSleepTile(int x, int y)
    {
        var location = Game1.currentLocation;
        var tile = new Vector2(x, y);
        var result = $"({x},{y}) mapPassable=" +
            location.isTilePassable(
                new xTile.Dimensions.Location(x, y), Game1.viewport);

        if (location.Objects.TryGetValue(tile, out var obj))
            result += $"; object={obj.Name}, passable={obj.isPassable()}";

        if (location.terrainFeatures.TryGetValue(tile, out var feature))
            result += $"; terrain={feature.GetType().Name}, passable={feature.isPassable()}";

        foreach (var furniture in location.furniture)
        {
            if (furniture.TileLocation == tile ||
                furniture.boundingBox.Value.Contains(x * 64 + 32, y * 64 + 32))
                result += $"; furniture={furniture.Name}, class={furniture.GetType().Name}, passable={furniture.isPassable()}";
        }
        return result;
    }

    private string SleepDate()
        => $"{Game1.year}/{Game1.currentSeason}/{Game1.dayOfMonth}";

    private CommandResponse SleepReply(GameCommand c, bool ok, string text)
        => new CommandResponse { Id = c.Id, Success = ok, Message = text };

    private CommandResponse ExecuteSleepStep(GameCommand c)
    {
        if (c.Params.ContainsKey("reset"))
        {
            ClearMovementState();
            sleepStartDate = SleepDate();
            sleepAnswered = false;
            sleepMoveIssued = false;
            sleepApproach = null;
            sleepEntered = false;
            sleepStarted = DateTime.UtcNow;
            nextSleepInput=default;sleepTransitionError="";morningReady=false;morningInputAttempts=0;morningGateObserved=false;
        }

        if (sleepStartDate == null)
            return SleepReply(c, false, "BLOCKED: sleep task not initialized.");

        UpdateSleepTransition();
        if(sleepTransitionError!="") return SleepReply(c,false,"BLOCKED: "+sleepTransitionError);

        if (sleepAnswered && SleepDate() != sleepStartDate &&
            Game1.timeOfDay >= 600 && Game1.timeOfDay < 1200 &&
            Game1.currentLocation is FarmHouse &&
            Game1.activeClickableMenu == null && Game1.player.CanMove && morningReady)
        {
            ClearMovementState();
            sleepAnswered=false;
            return SleepReply(c, true, "SLEEP_VERIFIED: next morning confirmed.");
        }

        if ((DateTime.UtcNow - sleepStarted).TotalSeconds > 180)
        {
            ClearMovementState();
            return SleepReply(c, false,
                "BLOCKED: 180 seconds elapsed. Check any overnight menu.");
        }

        if (sleepAnswered)
            return SleepReply(c, true,
                "WAIT: sleep accepted; waiting for next morning.");

        if (!(Game1.currentLocation is FarmHouse house))
            return SleepReply(c, false, "BLOCKED: enter your farmhouse first.");

        // Resolve the game's own player bed and sleeping spot.
        var bedMethod = house.GetType().GetMethod("GetPlayerBed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        var bed = bedMethod?.Invoke(house, null);
        if (bed == null)
            return SleepReply(c, false,
                "BLOCKED: GetPlayerBed unavailable or no player bed.");

        var spotMethod = bed.GetType().GetMethod("GetBedSpot",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        var spotValue = spotMethod?.Invoke(bed, null);
        Vector2 spot;
        if (spotValue is Point point)
            spot = new Vector2(point.X, point.Y);
        else if (spotValue is Vector2 vector)
            spot = vector;
        else
            return SleepReply(c, false,
                "BLOCKED: GetBedSpot unavailable; refusing to guess coordinates.");

        var playerTile = Game1.player.Tile;
        var distance = Math.Abs(playerTile.X - spot.X) +
                       Math.Abs(playerTile.Y - spot.Y);

        if (Game1.activeClickableMenu != null)
        {
            string question = house.lastQuestionKey ?? "";
            bool sleepQuestion = question == "Sleep" ||
                                 question.StartsWith("Sleep ");

            if (!(Game1.activeClickableMenu is DialogueBox) ||
                !sleepQuestion || distance > 2)
                return SleepReply(c, false,
                    $"BLOCKED: unrelated menu or not near bed. Question={question}");

            ClearMovementState();
            // Dispatch the ordinary Yes response only to the sleep question.
            Game1.activeClickableMenu = null;
            house.answerDialogue(new Response("Yes", "Yes"));
            sleepAnswered = true;
            return SleepReply(c, true,
                "SLEEP_ACCEPTED: selected Yes on the sleep confirmation.");
        }

        if (!sleepMoveIssued)
        {
            if (!Game1.player.CanMove)
                return SleepReply(c, true, "WAIT: player is busy.");

            // Approach only from left or right of the actual sleeping spot.
            var start = new Vector2(playerTile.X, playerTile.Y);
            var bedFurniture = bed as StardewValley.Objects.Furniture;
            if (bedFurniture == null)
                return SleepReply(c, false, "BLOCKED: bed is not furniture.");

            var bounds = bedFurniture.boundingBox.Value;
            int leftOutside = Math.Min(
                (int)bedFurniture.TileLocation.X - 1,
                (int)Math.Floor(bounds.Left / 64.0) - 1);
            int rightOutside = (int)Math.Ceiling(bounds.Right / 64.0);

            // Exterior approach positions, aligned with the sleeping row.
            List<Vector2>? bestPath = null;
            Vector2? bestSide = null;

            foreach (var side in new[] {
                new Vector2(leftOutside, spot.Y),
                new Vector2(rightOutside, spot.Y)
            })
            {
                var route = _pathfinder.FindPath(house, start, side);
                if (route != null &&
                    (bestPath == null || route.Count < bestPath.Count))
                {
                    bestPath = route;
                    bestSide = side;
                }
            }

            if (bestSide == null)
                return SleepReply(c, false,
                    $"BLOCKED: bedSpot=({spot.X},{spot.Y}), left=({leftOutside},{spot.Y}), right=({rightOutside},{spot.Y}), bounds={bounds}, player={playerTile}. Neither exterior approach has a path. LEFT: {DescribeSleepTile(leftOutside, (int)spot.Y)} RIGHT: {DescribeSleepTile(rightOutside, (int)spot.Y)} START: {DescribeSleepTile((int)playerTile.X, (int)playerTile.Y)}");

            sleepApproach = bestSide;
            sleepMoveIssued = true;

            var move = new GameCommand
            {
                Id = c.Id,
                Params = new Dictionary<string, object>
                {
                    ["x"] = (int)bestSide.Value.X,
                    ["y"] = (int)bestSide.Value.Y
                }
            };
            var result = ExecuteMoveTo(move);
            return SleepReply(c, result.Success,
                $"BED_SIDE=({bestSide.Value.X},{bestSide.Value.Y}); {result.Message}");
        }

        if (!sleepEntered && sleepApproach.HasValue &&
            playerTile.X == sleepApproach.Value.X &&
            playerTile.Y == sleepApproach.Value.Y &&
            Game1.player.CanMove)
        {
            // Only this final horizontal step may enter the bed.
            if (playerTile.Y != spot.Y || playerTile.X == spot.X)
                return SleepReply(c, false,
                    "BLOCKED: bed entry must be one horizontal step.");

            ClearMovementState();
            _currentPath = new List<Vector2>();
            int stepX = Math.Sign(spot.X - playerTile.X);
            for (int nextX = (int)playerTile.X + stepX;
                 stepX > 0 ? nextX <= (int)spot.X : nextX >= (int)spot.X;
                 nextX += stepX)
            {
                _currentPath.Add(new Vector2(nextX, spot.Y));
            }
            _pathIndex = 0;
            _finalTarget = spot;
            _stuckCounter = 0;
            _recalculationAttempts = 0;
            sleepEntered = true;

            return SleepReply(c, true,
                $"BED_ENTRY: horizontal step to ({spot.X},{spot.Y}).");
        }


        return SleepReply(c, true,
            $"WAIT: player=({playerTile.X},{playerTile.Y}), " +
            $"bed=({spot.X},{spot.Y}), moving={IsMoving}; waiting for sleep prompt.");
    }
}
