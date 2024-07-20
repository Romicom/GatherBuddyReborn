using System.Collections.Generic;
using System.Numerics;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;

namespace GatherBuddy.AutoGather.Tasks;

public interface IGatherTask
{
    public IEnumerable<Gatherable> DesiredGatherables { get; }
    public ILocation                Location           { get; }
    public GatheringType            GatheringType      { get; }
    public IEnumerable<Gatherable> IncompleteGatherables { get; }
}
