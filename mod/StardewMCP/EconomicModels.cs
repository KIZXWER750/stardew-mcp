using System;

namespace StardewMCP;

public sealed class CropProfitInput
{
    public int GrowthDays { get; set; }
    public int RegrowDays { get; set; }
    public int DaysRemaining { get; set; }
    public int Tiles { get; set; }
    public int SeedPrice { get; set; }
    public int UnitSellPrice { get; set; }
    public double ExpectedYieldPerHarvest { get; set; } = 1;
}

public sealed class CropProfitResult
{
    public int Harvests { get; set; }
    public int UpfrontCost { get; set; }
    public int ExpectedRevenue { get; set; }
    public int ExpectedProfit { get; set; }
    public int DaysToFirstHarvest { get; set; }
    public int WateringTileActions { get; set; }
}

public static class CropProfitMath
{
    public static CropProfitResult Calculate(CropProfitInput input)
    {
        if (input.GrowthDays < 1 || input.DaysRemaining < 0 || input.Tiles < 0 || input.SeedPrice < 0 || input.UnitSellPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(input));
        int harvests = input.GrowthDays > input.DaysRemaining ? 0 : 1;
        if (harvests > 0 && input.RegrowDays > 0)
            harvests += (input.DaysRemaining - input.GrowthDays) / input.RegrowDays;
        int cost = input.SeedPrice * input.Tiles;
        int revenue = (int)Math.Floor(input.UnitSellPrice * Math.Max(0, input.ExpectedYieldPerHarvest) * harvests * input.Tiles);
        return new CropProfitResult
        {
            Harvests = harvests, UpfrontCost = cost, ExpectedRevenue = revenue,
            ExpectedProfit = revenue - cost, DaysToFirstHarvest = input.GrowthDays,
            WateringTileActions = input.Tiles * Math.Min(input.GrowthDays, input.DaysRemaining)
        };
    }
}
