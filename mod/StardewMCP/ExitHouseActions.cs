using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace StardewMCP;

public partial class CommandExecutor
{
    private bool _exitHouseActive;
    private int _exitHouseTicks;
    private string _exitHouseStartLocation = "";

    private CommandResponse ExecuteExitHouse(GameCommand command)
    {
        if (!(Game1.currentLocation is FarmHouse))
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"EXIT_BLOCKED: current location is {Game1.currentLocation?.Name ?? "Unknown"}, not FarmHouse."
            };
        }

        var tile = Game1.player.Tile;

        // The exit warp is at (3,12). Start immediately above it.
        if ((int)tile.X != 3 || (int)tile.Y != 11)
        {
            return new CommandResponse
            {
                Id = command.Id,
                Success = false,
                Message = $"EXIT_BLOCKED: stand at (3,11) first. Current position=({(int)tile.X},{(int)tile.Y})."
            };
        }

        ClearMovementState();
        _exitHouseActive = true;
        _exitHouseTicks = 0;
        _exitHouseStartLocation = Game1.currentLocation.Name;

        return new CommandResponse
        {
            Id = command.Id,
            Success = true,
            Message = "EXIT_STARTED: holding the normal move-down button from (3,11) until the Farm warp activates."
        };
    }

    private void ProcessExitHouse()
    {
        if (!_exitHouseActive)
            return;

        _exitHouseTicks++;

        if (Game1.currentLocation?.Name != _exitHouseStartLocation)
        {
            _exitHouseActive = false;
            _exitHouseTicks = 0;
            _monitor.Log(
                $"EXIT_VERIFIED: entered {Game1.currentLocation?.Name} at ({(int)Game1.player.Tile.X},{(int)Game1.player.Tile.Y}).",
                LogLevel.Info
            );
            return;
        }

        if (_exitHouseTicks > 300)
        {
            _exitHouseActive = false;
            _monitor.Log(
                $"EXIT_BLOCKED: no location change after 300 ticks. Position=({(int)Game1.player.Tile.X},{(int)Game1.player.Tile.Y}).",
                LogLevel.Warn
            );
            return;
        }

        if (!Game1.player.CanMove)
            return;

        var down = Game1.options.moveDownButton.Length > 0
            ? Game1.options.moveDownButton[0].ToSButton()
            : SButton.S;

        _helper.Input.Press(down);
    }
}