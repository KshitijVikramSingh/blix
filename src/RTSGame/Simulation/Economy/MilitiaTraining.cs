using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Economy;

/// <summary>The one training recipe in the settlement-development slice.</summary>
/// <remarks>
/// One sack of timber and one sack of stone is simple enough to read on a person's back and gives both
/// gathering chains a military sink. The sixteen-second commitment is inherited from Providence's playable
/// prototype. Material remains physical at the barracks until the conversion completes; cancelling training
/// therefore leaves equipment at a place rather than manufacturing a refund or deleting it.
/// </remarks>
internal static class MilitiaTraining
{
    public const float Seconds = 16f;

    public static NodeStock Cost => new()
    {
        Wood = UnitType.Villager.CarryCapacity,
        Stone = UnitType.Villager.CarryCapacity,
    };

    public static int CostOf(Resource resource) => Cost[resource];

    public static bool CanPay(in EconomyNode barracks)
    {
        var cost = Cost;
        foreach (var resource in Resources.All)
        {
            if (barracks.Stock[resource] < cost[resource]) return false;
        }

        return true;
    }
}
