using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;

namespace GatherBuddy.AutoGather.Tasks;

public class GatherTask : IGatherTask
{
    public GatherTask(List<IGatherable> items, ILocation location, GatheringType type)
    {
        DesiredGatherables = items;
        Location           = location;
        GatheringType      = type;
    }

    public IEnumerable<IGatherable> DesiredGatherables { get; }
    public ILocation                Location           { get; }
    public GatheringType            GatheringType      { get; }
    
    public IEnumerable<IGatherable> IncompleteGatherables
        => DesiredGatherables.Where(g => g.InventoryCount < g.Quantity);
}
