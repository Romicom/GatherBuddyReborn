﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.SeFunctions;
using GatherBuddy.Time;
using GatherBuddy.Utility;
using CommandManager = GatherBuddy.SeFunctions.CommandManager;
using GatheringType = GatherBuddy.Enums.GatheringType;
using Lumina.Excel.GeneratedSheets2;
using Aetheryte = GatherBuddy.Classes.Aetheryte;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;
using Dalamud.Game.ClientState.Objects.Types;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJIGatheringNoteBookData;

namespace GatherBuddy.Plugin;

public class Executor
{
    private enum IdentifyType
    {
        None,
        Item,
        Fish,
    }

    private readonly CommandManager _commandManager = new(Dalamud.GameGui, Dalamud.SigScanner);
    private readonly MacroManager _macroManager = new();
    private readonly GatherBuddy _plugin;
    public readonly Identificator Identificator = new();

    public Executor(GatherBuddy plugin)
        => _plugin = plugin;

    private IdentifyType _identifyType = IdentifyType.None;
    private string _name = string.Empty;

    private IGatherable? _item = null;

    private GatheringType? _gatheringType = null;
    private ILocation? _location = null;
    private TimeInterval _uptime = TimeInterval.Always;

    public IGatherable? LastItem { get; private set; } = null;
    private readonly List<ILocation> _visitedLocations = new();
    private bool _keepVisitedLocations = false;
    private TimeStamp _lastGatherReset = TimeStamp.Epoch;
    private bool _teleporting = false;

    private void FindGatherableLogged(string itemName)
    {
        _item = Identificator.IdentifyGatherable(itemName);
        Communicator.PrintIdentifiedItem(itemName, _item);
    }

    private void FindFishLogged(string fishName)
    {
        _item = Identificator.IdentifyFish(fishName);
        Communicator.PrintIdentifiedItem(fishName, _item);
    }

    private void CheckVisitedLocations()
    {
        _lastGatherReset = GatherBuddy.Time.ServerTime.AddEorzeaHours(1);

        if (_keepVisitedLocations)
            _item = LastItem;
        else
            _visitedLocations.Clear();
        LastItem = _item;
        if (_item != null && _location != null)
            _visitedLocations.Add(_location);

        if ((LastItem?.Locations.Count() ?? 0) == _visitedLocations.Count)
            _visitedLocations.Clear();
    }

    private void HandleAlarm()
    {
        switch (_identifyType)
        {
            case IdentifyType.None: return;
            case IdentifyType.Item:
                _item = _plugin.AlarmManager.LastItemAlarm?.Item1.Item;
                return;
            case IdentifyType.Fish:
                _item = _plugin.AlarmManager.LastFishAlarm?.Item1.Item;
                return;
            default: throw new ArgumentOutOfRangeException();
        }
    }

    private void HandleNext()
    {
        _item = LastItem;
        if (_lastGatherReset < GatherBuddy.Time.ServerTime)
            _visitedLocations.Clear();
        _keepVisitedLocations = true;
        if (_item == null)
            Communicator.Print("No previous gather command registered.");
    }

    private void DoIdentify()
    {
        _keepVisitedLocations = false;
        if (_name.Length == 0)
            return;

        switch (_name)
        {
            case "alarm":
                HandleAlarm();
                return;
            case "next":
                HandleNext();
                return;
        }

        switch (_identifyType)
        {
            case IdentifyType.None: return;
            case IdentifyType.Item:
                FindGatherableLogged(_name);
                return;
            case IdentifyType.Fish:
                FindFishLogged(_name);
                return;
            default: throw new ArgumentOutOfRangeException();
        }
    }

    private void FindClosestLocation()
    {
        if (_item == null)
            return;

        if (!_item.Locations.Any())
        {
            Communicator.LocationNotFound(_item, _gatheringType);
            return;
        }

        _location = null;
        if (GatherBuddy.Config.PreferredGatheringType != GatheringType.Multiple
         && _gatheringType == null
         && _item is Gatherable { GatheringType: GatheringType.Multiple })
            _gatheringType = GatherBuddy.Config.PreferredGatheringType;

        (_location, _uptime) = (_keepVisitedLocations, _gatheringType) switch
        {
            (false, null) => GatherBuddy.UptimeManager.BestLocation(_item),
            (false, not null) => GatherBuddy.UptimeManager.NextUptime((Gatherable)_item, _gatheringType.Value, GatherBuddy.Time.ServerTime),
            (true, null) => GatherBuddy.UptimeManager.NextUptime(_item, GatherBuddy.Time.ServerTime, _visitedLocations),
            (true, not null) => GatherBuddy.UptimeManager.NextUptime((Gatherable)_item, _gatheringType.Value, GatherBuddy.Time.ServerTime,
                _visitedLocations),
        };

        if (_location == null)
            Communicator.LocationNotFound(_item, _gatheringType);
    }

    private void DoTeleport()
    {
        _teleporting = false;
        if (!GatherBuddy.Config.UseTeleport || _location?.ClosestAetheryte == null)
            return;

        if (GatherBuddy.Config.SkipTeleportIfClose
         && Dalamud.ClientState.TerritoryType == _location.Territory.Id
         && Dalamud.ClientState.LocalPlayer != null)
        {
            // Check distance of player to node against distance of aetheryte to node.
            var playerPos = Dalamud.ClientState.LocalPlayer.Position;
            var aetheryte = _location.ClosestAetheryte;
            var posX = Maps.NodeToMap(playerPos.X, _location.Territory.SizeFactor);
            var posY = Maps.NodeToMap(playerPos.Z, _location.Territory.SizeFactor);
            var distAetheryte = aetheryte != null
                ? System.Math.Sqrt(aetheryte.WorldDistance(_location.Territory.Id, _location.IntegralXCoord, _location.IntegralYCoord))
                : double.PositiveInfinity;
            var distPlayer = System.Math.Sqrt(Utility.Math.SquaredDistance(posX, posY, _location.IntegralXCoord, _location.IntegralYCoord));
            // Allow for some leeway due to teleport cost and time.
            if (distPlayer < distAetheryte * 1.5)
                return;
        }

        _teleporting = true;
        TeleportToAetheryte(_location.ClosestAetheryte);
    }

    private void DoGearChange()
    {
        if (!GatherBuddy.Config.UseGearChange || _location == null)
            return;

        var set = _location.GatheringType.ToGroup() switch
        {
            GatheringType.Fisher => GatherBuddy.Config.FisherSetName,
            GatheringType.Botanist => GatherBuddy.Config.BotanistSetName,
            GatheringType.Miner => GatherBuddy.Config.MinerSetName,
            _ => null,
        };
        if (set == null)
        {
            Communicator.PrintError("No job type associated with location ", _location.Name, GatherBuddy.Config.SeColorArguments, ".");
            return;
        }

        if (set.Length == 0)
        {
            Communicator.PrintError("No gear set for ", _location.GatheringType.ToString(), GatherBuddy.Config.SeColorArguments,
                " configured.");
            return;
        }

        var territory = _location.ClosestAetheryte?.Territory.Id ?? _location.Territory.Id;
        var time = DateTime.UtcNow.AddSeconds(30);
        var waitTime = DateTime.UtcNow.AddSeconds(_teleporting ? 6 : -1);

        void DoGearChangeOnArrival(object _)
        {
            if (DateTime.UtcNow < waitTime
             || Dalamud.Conditions[ConditionFlag.BetweenAreas]
             || Dalamud.Conditions[ConditionFlag.Casting]
             || territory != Dalamud.ClientState.TerritoryType)
                return;

            if (DateTime.UtcNow > time)
            {
                Dalamud.Framework.Update -= DoGearChangeOnArrival;
                return;
            }

            _commandManager.Execute($"/gearset change \"{set}\"");

            if (_item is Fish fish)
                GatherBuddy.CurrentBait.ChangeBait(fish.InitialBait.Id);

            Dalamud.Framework.Update -= DoGearChangeOnArrival;
        }

        Dalamud.Framework.Update += DoGearChangeOnArrival;
    }


    private unsafe void DoMapFlag()
    {
        if (!GatherBuddy.Config.WriteCoordinates && !GatherBuddy.Config.UseCoordinates || _location == null)
            return;

        if (_location.IntegralXCoord == 100 || _location.IntegralYCoord == 100)
            return;

        var instance = AgentMap.Instance();

        var link = new SeStringBuilder().AddFullMapLink(_location.Name, _location.Territory, _location.IntegralXCoord / 100f,
            _location.IntegralYCoord / 100f).BuiltString;

        Communicator.PrintCoordinates(link);

        if (instance != null)
        {
            if (GatherBuddy.Config.UseFlag)
                Maps.SetFlagMarker(instance, _location);

            if (GatherBuddy.Config.UseCoordinates)
            {
                var icon = GatherBuddy.GameData.GatheringIcons[_location.GatheringType];
                instance->TempMapMarkerCount = 0;
                instance->AddGatheringTempMarker(Maps.IntegerToInternal(_location.IntegralXCoord, _location.Territory.SizeFactor),
                    Maps.IntegerToInternal(_location.IntegralYCoord, _location.Territory.SizeFactor), _location.Radius, icon.Item1, 4u, _item?.Name[GatherBuddy.Language] ?? _location.Name);
                instance->OpenMap(_location.Territory.Data.Map.Row, _location.Territory.Id, _item?.Name[GatherBuddy.Language] ?? _location.Name,
                    MapType.GatheringLog);
            }
        }
    }

    private void DoAdditionalInfo()
    {
        Communicator.PrintUptime(_uptime);
    }

    private void DoWaymarks()
    {
        if (!GatherBuddy.Config.PlaceCustomWaymarks || _location == null)
            return;

        var territory = _location.Territory.Id;
        var markers = _location.Markers;
        if (Dalamud.ClientState.TerritoryType == territory)
        {
            GatherBuddy.WaymarkManager.SetWaymarks(markers);
            return;
        }

        var time = DateTime.UtcNow.AddSeconds(30);

        void DoWaymarkOnArrival(ushort t)
        {
            if (territory == t)
                GatherBuddy.WaymarkManager.SetWaymarks(markers);
            Dalamud.ClientState.TerritoryChanged -= DoWaymarkOnArrival;
        }

        Dalamud.ClientState.TerritoryChanged += DoWaymarkOnArrival;
    }

    public bool DoCommand(string argument)
    {
        if (Dalamud.ClientState.LocalPlayer == null || Dalamud.Conditions[ConditionFlag.BetweenAreas])
            return true;

        switch (argument)
        {
            case GatherBuddy.IdentifyCommand:
                DoIdentify();
                FindClosestLocation();
                CheckVisitedLocations();
                return true;
            case GatherBuddy.MapMarkerCommand:
                DoMapFlag();
                return true;
            case GatherBuddy.GearChangeCommand:
                DoGearChange();
                return true;
            case GatherBuddy.TeleportCommand:
                DoTeleport();
                return true;
            case GatherBuddy.AdditionalInfoCommand:
                DoAdditionalInfo();
                return true;
            case GatherBuddy.SetWaymarksCommand:
                DoWaymarks();
                return true;
            default: return false;
        }
    }
    
    private DateTime _teleportInitiated = DateTime.MinValue;

    public void AutoGather()
    {
        if (!GatherBuddy.Config.AutoGather) return;

        NavmeshStuckCheck();
        InventoryCheck();
        PreprocessDesiredNodeIds();

        var objs = Dalamud.ObjectTable.Where(g => g.ObjectKind == ObjectKind.GatheringPoint)
            .Where(g => g.IsTargetable)
            .Where(IsDesiredNode)
            .OrderBy(g => Vector3.Distance(g.Position, Dalamud.ClientState.LocalPlayer.Position));

        if (!objs.Any())
        {
            var activeItems = _plugin.GatherWindowManager.ActiveItems;
            if (!activeItems.Any())
            {
                GatherBuddy.Config.AutoGather = false;
                GatherBuddy.AutoStatus = "No active items.";
                GatherBuddy.Config.Save();
                return;
            }

            var item = activeItems.FirstOrDefault();
            if (item == null)
                return;

            var location = item.Locations.FirstOrDefault();
            if (location == null)
                return;

            if (location.Territory.Id != Dalamud.ClientState.TerritoryType && _teleportInitiated < DateTime.Now)
            {
                _teleportInitiated = DateTime.Now.AddSeconds(15);
                GatherBuddy.AutoStatus = "Teleporting to " + location.Territory.Name + "...";
                GatherItem(item);
                return;
            }
            else if (location.Territory.Id != Dalamud.ClientState.TerritoryType)
            {
                GatherBuddy.AutoStatus = "Waiting for teleport...";
                return;
            }
            else
            {
                //GatherBuddy.AutoStatus = "No nodes in area. Looking for known nodes...";
                Gatherable gatherable = item as Gatherable;
                if (gatherable == null)
                    return;

                var coords = gatherable.NodeList.SelectMany(n => n.WorldCoords.Values);
                var combinedCoords = coords.SelectMany(c => c).Distinct().ToList();
                if (!combinedCoords.Any())
                {
                    GatherBuddy.AutoStatus = "No known nodes for item " + item.Name[GatherBuddy.Language] + ".";
                    GatherBuddy.Config.AutoGather = false;
                    GatherBuddy.Config.Save();
                }
                else
                {
                    if (!Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        MountUp();
                    }
                    else
                    {
                        var closestCoord = combinedCoords.OrderBy(c => Vector3.Distance(c, Dalamud.ClientState.LocalPlayer.Position)).First();
                        if (GatherBuddy.Navmesh.IsReady() && !GatherBuddy.Navmesh.IsPathing())
                        {
                            GatherBuddy.AutoStatus = "Moving to known node " + item.Name[GatherBuddy.Language] + " at " + closestCoord + ".";
                            GatherBuddy.Navmesh.PathfindAndMoveTo(closestCoord, true);
                        }
                    }
                }
            }
        }
        else
        {
            var targetObj = objs.FirstOrDefault();
            if (targetObj == null)
                return;

            var distance = Vector3.Distance(targetObj.Position, Dalamud.ClientState.LocalPlayer.Position);
            if (distance < 3 && Dalamud.Conditions[ConditionFlag.Mounted])
            {
                GatherBuddy.AutoStatus = "Waiting for node harvest...";
                Dismount();
            }
            else if (distance > 3 && !Dalamud.Conditions[ConditionFlag.Mounted])
            {
                GatherBuddy.AutoStatus = "Mounting for travel...";
                MountUp();
            }
            else if (distance > 3 && Dalamud.Conditions[ConditionFlag.Mounted] && GatherBuddy.Navmesh.IsReady() && !GatherBuddy.Navmesh.IsPathing())
            {
                GatherBuddy.AutoStatus = $"Moving to node {targetObj.Name} at {targetObj.Position}";
                GatherBuddy.Navmesh.PathfindAndMoveTo(targetObj.Position, true);
            }
        }
    }


    private unsafe void InventoryCheck()
    {
        var presets = _plugin.GatherWindowManager.Presets;
        if (presets == null)
            return;

        var inventory = InventoryManager.Instance();
        if (inventory == null) return;

        foreach (var preset in presets)
        {
            var items = preset.Items;
            if (items == null)
                continue;

            var indicesToRemove = new List<int>();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var itemCount = inventory->GetInventoryItemCount(item.ItemId);
                if (itemCount >= item.Quantity)
                {
                    _plugin.GatherWindowManager.RemoveItem(preset, i);
                }
            }
        }
    }


    private DateTime _lastNavReset = DateTime.MinValue;
    private void NavmeshStuckCheck()
    {
        if (_lastNavReset.AddSeconds(10) < DateTime.Now)
        {
            _lastNavReset = DateTime.Now;
            GatherBuddy.Navmesh.Reload();
        }
    }

    private HashSet<uint> _desiredNodeIds;

    private void PreprocessDesiredNodeIds()
    {
        _desiredNodeIds = new HashSet<uint>();

        var activeItems = _plugin.GatherWindowManager.ActiveItems;
        if (activeItems == null)
            return;

        foreach (var item in activeItems)
        {
            if (item is Gatherable gatherable)
            {
                var nodes = gatherable.NodeList;
                if (nodes == null)
                    continue;

                foreach (var node in nodes)
                {
                    foreach (var coord in node.WorldCoords.Keys)
                    {
                        _desiredNodeIds.Add(coord);
                    }
                }
            }
        }

    }

    private bool IsDesiredNode(GameObject gameObject)
    {
        return _desiredNodeIds.Contains(gameObject.DataId);
    }


    private unsafe void Dismount()
    {
        var am = ActionManager.Instance();
        am->UseAction(ActionType.Mount, 0);
    }

    private unsafe void MountUp()
    {
        var am = ActionManager.Instance();
        var mount = GatherBuddy.Config.AutoGatherMountId;
        if (am->GetActionStatus(ActionType.Mount, mount) != 0) return;
        am->UseAction(ActionType.Mount, mount);
    }

    public void GatherLocation(ILocation location)
    {
        _identifyType = IdentifyType.None;
        _name = string.Empty;
        _item = null;
        _gatheringType = location.GatheringType.ToGroup();
        _location = location;
        if (location is GatheringNode n)
            _uptime = n.Times.NextUptime(GatherBuddy.Time.ServerTime);
        else
            _uptime = TimeInterval.Always;

        _macroManager.Execute();
    }

    public void GatherItem(IGatherable? item, GatheringType? type = null)
    {
        if (item == null)
            return;

        _identifyType = IdentifyType.None;
        _name = string.Empty;
        _item = item;
        _location = null;
        _gatheringType = type?.ToGroup();
        _uptime = TimeInterval.Always;

        _macroManager.Execute();
    }

    public void GatherFishByName(string fishName)
    {
        if (fishName.Length == 0)
            return;

        _identifyType = IdentifyType.Fish;
        _name = fishName.ToLowerInvariant();
        _item = null;
        _location = null;
        _gatheringType = null;
        _uptime = TimeInterval.Always;

        _macroManager.Execute();
    }

    public void GatherItemByName(string itemName, GatheringType? type = null)
    {
        if (itemName.Length == 0)
            return;

        _identifyType = IdentifyType.Item;
        _name = itemName.ToLowerInvariant();
        _item = null;
        _location = null;
        _gatheringType = type;
        _uptime = TimeInterval.Always;

        _macroManager.Execute();
    }

    public static void TeleportToAetheryte(Aetheryte aetheryte)
    {
        if (aetheryte.Id == 0)
            return;

        Teleporter.Teleport(aetheryte.Id);
    }

    public static void TeleportToTerritory(Territory territory)
    {
        if (territory.Aetherytes.Count == 0)
        {
            Communicator.PrintError(string.Empty, territory.Name, GatherBuddy.Config.SeColorArguments, " has no valid aetheryte.");
            return;
        }

        var aetheryte = territory.Aetherytes.FirstOrDefault(a => Teleporter.IsAttuned(a.Id));
        if (aetheryte == null)
        {
            Communicator.PrintError("Not attuned to any aetheryte in ", territory.Name, GatherBuddy.Config.SeColorArguments, ".");
            return;
        }

        Teleporter.TeleportUnchecked(aetheryte.Id);
    }
}
