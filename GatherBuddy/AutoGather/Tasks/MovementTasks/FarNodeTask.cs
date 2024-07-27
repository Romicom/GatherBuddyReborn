using System;
using ECommons.Automation.NeoTaskManager;

namespace GatherBuddy.AutoGather.Tasks.MovementTasks;

public class FarNodeTask() : TaskManagerTask(DefaultFunction, DefaultConfiguration)
{
    private static Func<bool?> DefaultFunction = () =>
    {
        // Implement task logic here
        return true;
    };

    // This is default configuration for FarNodeTask
    // Needed task configuration must be set here
    private static TaskManagerConfiguration DefaultConfiguration = new TaskManagerConfiguration()
    {
        TimeLimitMS    = 1000,
        AbortOnTimeout = true,
        // set other properties if needed
    };
}
