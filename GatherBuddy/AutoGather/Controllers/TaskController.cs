using System.Collections.Generic;
using GatherBuddy.AutoGather.Tasks;
using GatherBuddy.Interfaces;

namespace GatherBuddy.AutoGather.Controllers;

public class TaskController
{
    private readonly GatherBuddy _plugin;
    public TaskController(GatherBuddy plugin)
    {
        _plugin                                =  plugin;
        GatherBuddy.UptimeManager.UptimeChange += UptimeChangeHandler;
    }

    public IEnumerable<IGatherTask> Tasks = new List<IGatherTask>();

    private List<IGatherable> _gatherWindowItems
        => _plugin.GatherWindowManager.ActiveItems;

    private void UptimeChangeHandler(IGatherable obj)
    {
        
    }
}
