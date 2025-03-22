using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.SeFunctions;
using GatherBuddy.Data;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private (IGameObject? Node, Gatherable? Item) _currentClosestNode;
        private (IGameObject? Node, Gatherable? Item) _currentNode;

        private (IGameObject? Node, Gatherable? Item) FindTargetNode()
        {
            return GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode 
                ? FindClosestNode() 
                : FindFirstNode();
        }

        private (IGameObject? Node, Gatherable? Item) FindClosestNode()
        {
            IGameObject? closestNode = null;
            Gatherable? closestItem = null;
            float minDistance = float.MaxValue;

            foreach (var item in _targetItem)
            {
                if (!GatherBuddy.GameData.GatherableToNode.TryGetValue(item, out var nodes))
                    continue;

                foreach (var node in nodes)
                {
                    var nodeObj = GameObjectHelper.GetClosestNode(node);
                    if (nodeObj == null || !nodeObj.IsTargetable)
                        continue;

                    var distance = nodeObj.Position.DistanceToPlayer();
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        closestNode = nodeObj;
                        closestItem = item;
                    }
                }
            }
            return (closestNode, closestItem);
        }

        private (IGameObject? Node, Gatherable? Item) FindFirstNode()
        {
            foreach (var item in _targetItem)
            {
                if (!GatherBuddy.GameData.GatherableToNode.TryGetValue(item, out var nodes))
                    continue;

                var nodeObj = GameObjectHelper.GetClosestNode(nodes.First());
                if (nodeObj != null && nodeObj.IsTargetable)
                    return (nodeObj, item);
            }
            return (null, null);
        }

        private unsafe void EnqueueDismount()
        {
            // First stop any ongoing navigation
            TaskManager.Enqueue(StopNavigation);

            var am = ActionManager.Instance();

            // First dismount attempt
            TaskManager.Enqueue(() => {
                if (Dalamud.Conditions[ConditionFlag.Mounted])
                    am->UseAction(ActionType.Mount, 0);
            }, "Dismount");

            // Wait until we're not in flight and can act
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.InFlight] && CanAct, 1000, "Wait for not in flight");

            // Second dismount attempt in case the first one failed
            TaskManager.Enqueue(() => {
                if (Dalamud.Conditions[ConditionFlag.Mounted])
                    am->UseAction(ActionType.Mount, 0);
            }, "Dismount 2");

            // Wait until we're fully dismounted and can act
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Mounted] && CanAct, 1000, "Wait for dismount");

            // Add a small delay after dismounting to ensure stability
            TaskManager.Enqueue(() => {
                if (!Dalamud.Conditions[ConditionFlag.Mounted])
                    TaskManager.DelayNextImmediate(500);
            });
        }

        private unsafe void EnqueueMountUp()
        {
            // Don't try to mount if we're already mounted
            if (Dalamud.Conditions[ConditionFlag.Mounted])
                return;

            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
            Action doMount;

            // Try to use the configured mount if it's unlocked and available
            if (IsMountUnlocked(mount) && am->GetActionStatus(ActionType.Mount, mount) == 0)
            {
                doMount = () => am->UseAction(ActionType.Mount, mount);
            }
            else
            {
                // Fall back to the random mount action if the specific mount isn't available
                if (am->GetActionStatus(ActionType.GeneralAction, 24) != 0)
                {
                    // If we can't mount at all, log a warning and return
                    GatherBuddy.Log.Warning("Cannot mount up - mount action unavailable");
                    return;
                }

                doMount = () => am->UseAction(ActionType.GeneralAction, 24);
            }

            // Stop any current navigation before mounting
            TaskManager.Enqueue(StopNavigation);

            // Use the mount action with appropriate delay
            EnqueueActionWithDelay(doMount);

            // Wait for the mounted status with a timeout
            TaskManager.Enqueue(() => Svc.Condition[ConditionFlag.Mounted], 2000, "Wait for mount");
        }

        private unsafe bool IsMountUnlocked(uint mount)
        {
            var instance = PlayerState.Instance();
            return instance != null && instance->IsMountUnlocked(mount);
        }

        private void MoveToCloseNode(ConfigPreset config)
        {
            var targetNode = FindTargetNode();

            // Store the target node in the appropriate variable
            if (GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode)
                _currentClosestNode = targetNode;
            else
                _currentNode = targetNode;

            // Get the current node based on the configuration
            var (currentNode, currentItem) = GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode
                ? _currentClosestNode
                : _currentNode;

            // Return early if no valid node or item was found
            if (currentNode == null || currentItem == null)
                return;

            var distance = currentNode.Position.DistanceToPlayer();

            if (distance < 3)
            {
                var waitGP = currentItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.CollectableMinGP;
                waitGP |= !currentItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.GatherableMinGP;

                if (Dalamud.Conditions[ConditionFlag.Mounted] && (waitGP || Dalamud.Conditions[ConditionFlag.InFlight] || GetConsumablesWithCastTime(config) > 0))
                {
                    EnqueueDismount();
                    TaskManager.Enqueue(() => {
                        if (Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            try
                            {
                                var floor = VNavmesh_IPCSubscriber.Query_Mesh_PointOnFloor(Player.Position, false, 3);
                                Navigate(floor, true);
                                TaskManager.Enqueue(() => !IsPathGenerating);
                                TaskManager.Enqueue(() => !IsPathing, 1000);
                                EnqueueDismount();
                            }
                            catch { }
                            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) _advancedUnstuck.Force(); });
                        }
                    });
                }
                else if (waitGP)
                {
                    StopNavigation();
                    AutoStatus = "Waiting for GP to regenerate...";
                }
                else
                {
                    // Check if we need to use a consumable with cast time
                    uint consumable = GetConsumablesWithCastTime(config);
                    if (consumable > 0)
                    {
                        // Stop navigation if we're moving
                        if (IsPathing)
                        {
                            StopNavigation();
                            // Add a small delay to ensure we've fully stopped
                            TaskManager.DelayNext(200);
                        }
                        else
                        {
                            // Use the consumable if we're not already casting something
                            if (!Dalamud.Conditions[ConditionFlag.Casting] && !Dalamud.Conditions[ConditionFlag.Casting87])
                            {
                                EnqueueActionWithDelay(() => UseItem(consumable));
                            }
                        }
                    }
                    else
                    {
                        // No consumables needed, interact with the node
                        EnqueueNodeInteraction();

                        // If we're not underwater, navigate to the node if we're not already gathering
                        if (!Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            TaskManager.Enqueue(() => {
                                if (!Dalamud.Conditions[ConditionFlag.Gathering] && !Dalamud.Conditions[ConditionFlag.Gathering42])
                                    Navigate(currentNode.Position, false);
                            });
                        }
                    }
                }
            }
            else if (distance < Math.Max(GatherBuddy.Config.AutoGatherConfig.MountUpDistance, 5) && !Dalamud.Conditions[ConditionFlag.Diving])
            {
                Navigate(currentNode.Position, false);
            }
            else
            {
                if (!Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    EnqueueMountUp();
                }
                else
                {
                    Navigate(currentNode.Position, ShouldFly(currentNode.Position));
                }
            }
        }

        private Vector3? lastPosition = null;
        private DateTime lastMovementTime;
        private DateTime lastResetTime;

        private void StopNavigation()
        {
            CurrentDestination = default;
            if (VNavmesh_IPCSubscriber.IsEnabled)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
            }
            lastResetTime = DateTime.Now;
        }

        private void Navigate(Vector3 destination, bool shouldFly)
        {
            // If we're already navigating to this destination, don't restart navigation
            if (CurrentDestination == destination && (IsPathing || IsPathGenerating))
                return;

            // Safety check: don't try to navigate underwater without being mounted
            if (Dalamud.Conditions[ConditionFlag.Diving] && !Dalamud.Conditions[ConditionFlag.Mounted])
            {
                GatherBuddy.Log.Error("Navigate() called underwater without mounting up first");
                // Instead of disabling the entire feature, try to mount up first
                EnqueueMountUp();
                // Store the destination for later use after mounting
                CurrentDestination = destination;
                return;
            }

            // Always fly when underwater
            shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

            // Stop any current navigation before starting a new one
            StopNavigation();
            CurrentDestination = destination;

            // Get a corrected destination that's navigable
            var correctedDestination = GetCorrectedDestination(CurrentDestination);
            GatherBuddy.Log.Debug($"Navigating to {destination} (corrected to {correctedDestination})");

            // Start the actual navigation
            LastNavigationResult = VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(correctedDestination, shouldFly);
        }

        private static Vector3 GetCorrectedDestination(Vector3 destination)
        {
            // Start with the original destination
            var correctedDestination = destination;

            // Check if we have a predefined offset for this node position
            if (WorldData.NodeOffsets.TryGetValue(destination, out var offset))
                correctedDestination = offset;

            try
            {
                // Try to find the nearest navigable point on the mesh
                var nearestPoint = VNavmesh_IPCSubscriber.Query_Mesh_NearestPoint(correctedDestination, 3, 3);

                // Only use the corrected point if it's reasonably close to our target
                if (Vector3.Distance(nearestPoint, destination) is var distance and <= 3)
                {
                    correctedDestination = nearestPoint;
                }
                else
                {
                    // If the offset took us too far, log a warning and try again with the original position
                    GatherBuddy.Log.Warning($"Offset ignored (distance {distance} too large)");
                    correctedDestination = VNavmesh_IPCSubscriber.Query_Mesh_NearestPoint(destination, 3, 3);
                }
            }
            catch (Exception ex)
            {
                // If there's an error with the navmesh query, log it but continue with the uncorrected destination
                GatherBuddy.Log.Error($"Error finding nearest point on navmesh: {ex.Message}");
            }

            return correctedDestination;
        }

        private void MoveToFarNode(Vector3 position)
        {
            if (!Dalamud.Conditions[ConditionFlag.Mounted])
            {
                EnqueueMountUp();
            }
            else
            {
                Navigate(position, ShouldFly(position));
            }
        }

        private bool MoveToTerritory(ILocation location)
        {
            var aetheryte = location.ClosestAetheryte;
            var territory = location.Territory;

            if (ForcedAetherytes.ZonesWithoutAetherytes.FirstOrDefault(x => x.ZoneId == territory.Id).AetheryteId is var alt && alt > 0)
                territory = GatherBuddy.GameData.Aetherytes[alt].Territory;

            if (aetheryte == null || !Teleporter.IsAttuned(aetheryte.Id) || aetheryte.Territory != territory)
            {
                aetheryte = territory.Aetherytes
                    .Where(a => Teleporter.IsAttuned(a.Id))
                    .OrderBy(a => a.WorldDistance(territory.Id, location.IntegralXCoord, location.IntegralYCoord))
                    .FirstOrDefault();
            }

            if (aetheryte == null)
            {
                Communicator.PrintError("No attuned aetheryte found");
                return false;
            }

            EnqueueActionWithDelay(() => Teleporter.Teleport(aetheryte.Id));
            TaskManager.Enqueue(() => Svc.Condition[ConditionFlag.BetweenAreas]);
            TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.BetweenAreas]);
            TaskManager.DelayNext(1500);

            return true;
        }
    }
}
