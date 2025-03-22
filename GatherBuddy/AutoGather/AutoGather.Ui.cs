using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;
using ImGuiNET;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.CustomInfo;
using Newtonsoft.Json;
using OtterGui;
using OtterGui.Raii;

namespace GatherBuddy.AutoGather
{
    public static class AutoGatherUI
    {
        private static bool _gatherDebug;

        public static void DrawAutoGatherStatus()
        {
            var enabled = GatherBuddy.AutoGather.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
            {
                GatherBuddy.AutoGather.Enabled = enabled;
            }

            // Prioritization checkbox
            var prioritizeClosest = GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode;
            if (ImGui.Checkbox("Prioritize Closest Node", ref prioritizeClosest))
            {
                GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode = prioritizeClosest;
                GatherBuddy.Config.Save();
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("When enabled, will path to the closest node from your list instead of the first one");

            ImGui.Text($"Status: {GatherBuddy.AutoGather.AutoStatus}");
            var lastNavString = GatherBuddy.AutoGather.LastNavigationResult.HasValue
                ? GatherBuddy.AutoGather.LastNavigationResult.Value
                    ? "Successful"
                    : "Failed (If you're seeing this you probably need to restart your game)"
                : "None";
            ImGui.Text($"Navigation: {lastNavString}");
        }

        public static void DrawDebugTables()
        {
            if (ImGui.Button("Import Node Offsets from Clipboard"))
            {
                var settings = new JsonSerializerSettings();
                var text = ImGuiUtil.GetClipboardText();

                try
                {
                    var vectors = JsonConvert.DeserializeObject<List<OffsetPair>>(text, settings) ?? [];
                    foreach (var offset in vectors)
                    {
                        WorldData.NodeOffsets[offset.Original] = offset.Offset;
                        GatherBuddy.Log.Information($"Added offset {offset} to dictionary");
                    }
                    WorldData.SaveOffsetsToFile();
                    GatherBuddy.Log.Information("Import complete");
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Error($"Failed to import node offsets: {ex.Message}");
                    Communicator.PrintError("[GatherBuddyReborn] Failed to import node offsets. Check the clipboard content format.");
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Export Node Offsets to Clipboard"))
            {
                try
                {
                    var settings = new JsonSerializerSettings();
                    var offsetPairs = WorldData.NodeOffsets?.Select(x => new OffsetPair(x.Key, x.Value)).ToList() ?? new List<OffsetPair>();
                    var offsetString = JsonConvert.SerializeObject(offsetPairs, Formatting.Indented, settings);
                    ImGui.SetClipboardText(offsetString);
                    GatherBuddy.Log.Information("Node offsets exported to clipboard");
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Error($"Failed to export node offsets: {ex.Message}");
                    Communicator.PrintError("[GatherBuddyReborn] Failed to export node offsets.");
                }
            }
            // First column: Nearby nodes table
            if (ImGui.BeginTable("##nearbyNodesTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Targetable");
                ImGui.TableSetupColumn("NodeId");
                ImGui.TableSetupColumn("Position");
                ImGui.TableSetupColumn("Distance");
                ImGui.TableSetupColumn("Action");

                ImGui.TableHeadersRow();

                if (Player.Object == null)
                {
                    ImGui.Text("Player not available");
                    ImGui.EndTable();
                    return;
                }

                var playerPosition = Player.Object.Position;
                var objectList = Svc.Objects?.Where(o => o != null && o.ObjectKind == ObjectKind.GatheringPoint);

                if (objectList == null)
                {
                    ImGui.Text("No gathering points found");
                    ImGui.EndTable();
                    return;
                }

                foreach (var node in objectList.OrderBy(o => Vector3.Distance(o.Position, playerPosition)))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(node.Name.ToString());
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(node.IsTargetable ? "Y" : "N");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(node.DataId.ToString());
                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text(node.Position.ToString());
                    ImGui.TableSetColumnIndex(4);
                    var distance = Vector3.Distance(playerPosition, node.Position);
                    ImGui.Text(distance.ToString("F2"));
                    ImGui.TableSetColumnIndex(5);

                    var territoryId = Dalamud.ClientState.TerritoryType;
                    var isBlacklisted = GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.TryGetValue(territoryId, out var list)
                     && list.Contains(node.Position);

                    if (isBlacklisted)
                    {
                        if (ImGui.Button($"Unblacklist##{node.Position}"))
                        {
                            list.Remove(node.Position);
                            if (list.Count == 0)
                            {
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.Remove(territoryId);
                            }

                            GatherBuddy.Config.Save();
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"Blacklist##{node.Position}"))
                        {
                            if (list == null)
                            {
                                list                                                                           = new List<Vector3>();
                                GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId[territoryId] = list;
                            }

                            list.Add(node.Position);
                            GatherBuddy.Config.Save();
                        }
                    }

                    if (ImGui.Button($"Navigate##{node.Position}"))
                    {
                        if (GatherBuddy.AutoGather.Enabled)
                        {
                            Communicator.PrintError("[GatherBuddyReborn] Auto-Gather is enabled! Unable to navigate.");
                        }
                        else
                        {
                            VNavmesh_IPCSubscriber.Path_Stop();
                            VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(node.Position, GatherBuddy.AutoGather.ShouldFly(node.Position));
                        }
                    }

                    if (WorldData.NodeOffsets.TryGetValue(node.Position, out var offset))
                    {
                        if (ImGui.Button($"Remove Offset##{node.Position}"))
                        {
                            WorldData.NodeOffsets.Remove(node.Position);
                            WorldData.SaveOffsetsToFile();
                        }
                        ImGui.Text(offset.ToString());
                        if (ImGui.Button($"Navigate to Offset##{node.Position}"))
                        {
                            if (GatherBuddy.AutoGather.Enabled)
                            {
                                Communicator.PrintError("[GatherBuddyReborn] Auto-Gather is enabled! Unable to navigate.");
                            }
                            else
                            {
                                VNavmesh_IPCSubscriber.Path_Stop();
                                VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(offset, GatherBuddy.AutoGather.ShouldFly(offset));
                            }
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"Add Offset##{node.Position}"))
                        {
                            WorldData.AddOffset(node.Position, playerPosition);
                        }
                        ImGui.Text(playerPosition.ToString());
                    }
                    
                }

                ImGui.EndTable();
            }
        }

        public unsafe static void DrawMountSelector()
        {
            ImGui.PushItemWidth(300);
            var ps = PlayerState.Instance();

            // Fix potential null reference by using FirstOrDefault and checking for null
            var mountSheet = Dalamud.GameData.GetExcelSheet<Mount>();
            var selectedMount = mountSheet?.FirstOrDefault(x => x.RowId == GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId);

            string preview = "Mount Roulette";
            if (selectedMount != null && selectedMount.Singular != null)
            {
                preview = selectedMount.Singular.ToString().ToProperCase();
            }

            if (ImGui.BeginCombo("Select Mount", preview))
            {
                if (ImGui.Selectable("Mount Roulette", GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId == 0))
                {
                    GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId = 0;
                    GatherBuddy.Config.Save();
                }

                if (mountSheet != null)
                {
                    foreach (var mount in mountSheet.OrderBy(x => x.Singular?.ToString().ToProperCase() ?? string.Empty))
                    {
                        if (mount.Singular != null && ps != null && ps->IsMountUnlocked(mount.RowId))
                        {
                            var selected = ImGui.Selectable(mount.Singular.ToString().ToProperCase(),
                                GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId == mount.RowId);

                            if (selected)
                            {
                                GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId = mount.RowId;
                                GatherBuddy.Config.Save();
                            }
                        }
                    }
                }

                ImGui.EndCombo();
            }

            // Balance the PushItemWidth with a PopItemWidth
            ImGui.PopItemWidth();
        }

        public static string ToProperCase(this string input)
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower());
        }
    }
}
