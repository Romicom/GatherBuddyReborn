using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;
using System.Linq;
using System.Runtime.InteropServices;
using ECommons.Automation.UIInput;
using ItemSlot = GatherBuddy.AutoGather.GatheringTracker.ItemSlot;
using Dalamud.Game.ClientState.Conditions;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueNodeInteraction()
        {
            var (node, item) = GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode
                ? _currentClosestNode
                : _currentNode;

            if (node == null || item == null)
                return;

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;

            TaskManager.Enqueue(() => targetSystem->OpenObjectInteraction(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)node.Address));
            // Wait for the gathering condition to be true with a timeout of 500ms
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Gathering], 500);
        }

        private unsafe void EnqueueGatherItem(ItemSlot slot)
        {
            if (GatheringAddon == null)
                return;

            if (slot.Item == null)
                return;

            var itemIndex = slot.Index;
            var receiveEventAddress = new nint(GatheringAddon->AtkUnitBase.AtkEventListener.VirtualTable->ReceiveEvent);
            var eventDelegate = Marshal.GetDelegateForFunctionPointer<ClickHelper.ReceiveEventDelegate>(receiveEventAddress);

            var target = AtkStage.Instance();
            var eventData = EventData.ForNormalTarget(target, &GatheringAddon->AtkUnitBase);
            var inputData = InputData.Empty();

            EnqueueActionWithDelay(() => eventDelegate.Invoke(&GatheringAddon->AtkUnitBase.AtkEventListener,
                EventType.CHANGE, (uint)itemIndex, eventData.Data, inputData.Data));

            if (slot.Item.IsTreasureMap)
            {
                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Gathering42], 1000);
                TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Gathering42]);
                TaskManager.Enqueue(RefreshNextTreasureMapAllowance);
            }
        }

        private (bool UseSkills, ItemSlot Slot) GetItemSlotToGather(Gatherable? desiredItem = null)
        {
            // Safely get the target item, handling potential null references
            var targetItem = GatherBuddy.Config.AutoGatherConfig.PrioritizeClosestNode
                ? (_currentClosestNode.node != null ? _currentClosestNode.Item : null)
                : (_currentNode.node != null ? _currentNode.Item : null);

            // If a specific item is desired, prioritize it
            if (desiredItem != null)
            {
                targetItem = desiredItem;
            }

            // Original crystal gathering logic
            if (HasGivingLandBuff)
            {
                var crystal = GetAnyCrystalInNode();
                if (crystal != null)
                    return (true, crystal);
            }

            var available = NodeTracker.Available
                .Where(CheckItemOvercap)
                .ToList();

            // Dual-mode target handling
            var target = targetItem != null
                ? available.FirstOrDefault(s => s.Item != null && s.Item == targetItem)
                : null;

            if (target != null && targetItem != null && InventoryCount(targetItem) < QuantityTotal(targetItem))
            {
                return (!target.Collectable, target);
            }

            // Original fallback logic - ensure we don't have null items in the join
            var gatherList = ItemsToGather
                .Where(i => i.Item != null)
                .Join(available.Where(s => s.Item != null), i => i.Item, s => s.Item, (i, s) => s)
                .Where(s => s.Item != null && InventoryCount(s.Item) < QuantityTotal(s.Item));

            // Ensure we don't have null items in the fallback list join
            var fallbackList = _plugin.AutoGatherListsManager.FallbackItems
                .Where(i => i.Item != null)
                .Join(available.Where(s => s.Item != null), i => i.Item, s => s.Item, (i, s) => (Slot: s, i.Quantity))
                .Where(x => x.Slot.Item != null && InventoryCount(x.Slot.Item) < x.Quantity)
                .Select(x => x.Slot);

            var fallbackSkills = GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems;

            var slot = gatherList.FirstOrDefault();
            if (slot != null)
            {
                return (!slot.Collectable, slot);
            }

            slot = fallbackList.FirstOrDefault();
            if (slot != null)
            {
                return (fallbackSkills && !slot.Collectable, slot);
            }

            if (GatherBuddy.Config.AutoGatherConfig.AbandonNodes)
                throw new NoGatherableItemsInNodeException();

            if (target != null)
            {
                return (false, target);
            }

            slot = GetAnyCrystalInNode();
            if (slot != null)
            {
                return (false, slot);
            }

            slot = available.FirstOrDefault(s => s.Item != null && !s.Item.IsTreasureMap && !s.Collectable);
            return slot != null
                ? (false, slot)
                : throw new NoGatherableItemsInNodeException();
        }

        private bool CheckItemOvercap(ItemSlot s)
        {
            // Early return if the slot or item is null
            if (s == null || s.Item == null)
                return false;

            // Check if it's a treasure map and we already have one
            if (s.Item.IsTreasureMap && InventoryCount(s.Item) != 0)
                return false;

            // Check if it's a crystal and we're near the cap
            if (s.Item.IsCrystal && InventoryCount(s.Item) > 9999 - s.Yield)
                return false;

            return true;
        }
        
        private ItemSlot? GetAnyCrystalInNode()
        {
            if (NodeTracker?.Available == null)
                return null;

            return NodeTracker.Available
                .Where(s => s != null && s.Item != null && s.Item.IsCrystal)
                .Where(CheckItemOvercap)
                .OrderBy(s => ItemsToGather != null && s.Item != null && ItemsToGather.Any(g => g?.Item == s.Item) ? 0 : 1)
                .ThenBy(s => s.Item != null ? InventoryCount(s.Item) : int.MaxValue)
                .FirstOrDefault();
        }
    }
}
