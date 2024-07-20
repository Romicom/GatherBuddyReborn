using ECommons.Automation.LegacyTaskManager;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public AutoGather(GatherBuddy plugin)
        {
            // Initialize the task manager
            TaskManager = new();
            _plugin     = plugin;
        }

        private GatherBuddy _plugin;

        public TaskManager TaskManager { get; }

        private bool _enabled { get; set; } = false;

        public unsafe bool Enabled
        {
            get => _enabled;
            set
            {
                if (!value)
                {
                    //Do Reset Tasks
                    var gatheringMasterpiece = MasterpieceAddon;
                    if (gatheringMasterpiece != null && !gatheringMasterpiece->AtkUnitBase.IsVisible)
                    {
                        gatheringMasterpiece->AtkUnitBase.IsVisible = true;
                    }

                    if (IsPathing || IsPathGenerating)
                    {
                        VNavmesh_IPCSubscriber.Path_Stop();
                    }

                    TaskManager.Abort();
                    GatherTasks.Clear();
                    HasSeenFlag    = false;
                    AutoStatus     = "Idle...";
                }

                if (value)
                {
                    BuildTaskList();
                }

                _enabled = value;
            }
        }

        public void DoAutoGather()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                if (!NavReady && Enabled)
                {
                    AutoStatus = "Waiting for Navmesh...";
                    return;
                }
            }
            catch (Exception e)
            {
                //GatherBuddy.Log.Error(e.Message);
                AutoStatus = "vnavmesh communication failed. Do you have it installed??";
                return;
            }

            DoSafetyChecks();
            if (TaskManager.NumQueuedTasks > 0)
            {
                //GatherBuddy.Log.Verbose("TaskManager has tasks, skipping DoAutoGather");
                return;
            }

            if (GatherTasks.Count < 1)
            {
                AutoStatus         = "No items to gather...";
                Enabled            = false;
                CurrentDestination = null;
                VNavmesh_IPCSubscriber.Path_Stop();
                return;
            }

            if (!CanAct)
            {
                AutoStatus = "Player is busy...";
                return;
            }

            var task = OrderedTasks.FirstOrDefault();
            if (task == null)
            {
                Enabled = false;
                Communicator.PrintError("Nothing to auto-gather");
                return;
            }
            

            AutoStatus = "Nothing to do...";
        }

        private void DoSafetyChecks()
        {
            if (VNavmesh_IPCSubscriber.Path_GetAlignCamera())
            {
                GatherBuddy.Log.Warning("VNavMesh Align Camera Option turned on! Forcing it off for GBR operation.");
                VNavmesh_IPCSubscriber.Path_SetAlignCamera(false);
            }
        }
    }
}
