using System.Collections.Generic;
using System.Linq;
using ECommons;
using GatherBuddy.AutoGather.Tasks;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    public List<IGatherTask> GatherTasks = [];

    public List<IGatherTask> OrderedTasks = GatherTasks.OrderBy(t => t.Location).ThenBy(t => t.GatheringType).ToList();
    public void BuildTaskList()
    {
        GatherTasks.Clear();
        
        var items = _plugin.GatherWindowManager.ActiveItems;
        foreach (var item in items)
        {
            if (GatherBuddy.UptimeManager.TimedGatherables.Contains(item))
            {
                //TODO: Handle Timed Nodes Separately
                return;
            }
            var location = _plugin.Executor.FindClosestLocation(item);
            if (location == null)
            {
                Communicator.PrintError($"No location found for {item.Name[GatherBuddy.Language]}");
                return;
            }

            var gatheringType = DetermineGatheringType(item);
            var existingTask  = GatherTasks.FirstOrDefault(t => t.Location == location && t.GatheringType == gatheringType);
            if (existingTask == null)
            {
                var list = new List<IGatherable>();
                list.Add(item);
                GatherTasks.Add(new GatherTask(list, location, gatheringType));
            }
            else
            {
                existingTask.DesiredGatherables.Add(item);
            }
        }
    }

    private GatheringType DetermineGatheringType(IGatherable item)
    {
        if (item is Gatherable gatherable)
        {
            return gatherable.GatheringType.ToGroup();
        }

        if (item is Fish fish)
        {
            return GatheringType.Fisher;
        }

        return GatheringType.Unknown;
    }
}
