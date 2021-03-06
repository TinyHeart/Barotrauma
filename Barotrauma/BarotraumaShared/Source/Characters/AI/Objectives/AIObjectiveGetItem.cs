﻿using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    class AIObjectiveGetItem : AIObjective
    {
        public override string DebugTag => "get item";

        private readonly bool equip;
        public HashSet<Item> ignoredItems = new HashSet<Item>();

        public Func<Item, float> GetItemPriority;

        //can be either tags or identifiers
        private string[] itemIdentifiers;
        public IEnumerable<string> Identifiers => itemIdentifiers;
        private Item targetItem, moveToTarget;
        public Item TargetItem => targetItem;
        private int currSearchIndex;
        public string[] ignoredContainerIdentifiers;
        private AIObjectiveGoTo goToObjective;
        private float currItemPriority;

        public bool AllowToFindDivingGear { get; set; } = true;

        public override float GetPriority()
        {
            if (objectiveManager.CurrentOrder == this)
            {
                return AIObjectiveManager.OrderPriority;
            }
            return 1.0f;
        }

        public AIObjectiveGetItem(Character character, Item targetItem, AIObjectiveManager objectiveManager, bool equip = true, float priorityModifier = 1) 
            : base(character, objectiveManager, priorityModifier)
        {
            currSearchIndex = -1;
            this.equip = equip;
            this.targetItem = targetItem;
        }

        public AIObjectiveGetItem(Character character, string itemIdentifier, AIObjectiveManager objectiveManager, bool equip = true, bool checkInventory = true, float priorityModifier = 1) 
            : this(character, new string[] { itemIdentifier }, objectiveManager, equip, checkInventory, priorityModifier) { }

        public AIObjectiveGetItem(Character character, string[] itemIdentifiers, AIObjectiveManager objectiveManager, bool equip = true, bool checkInventory = true, float priorityModifier = 1) 
            : base(character, objectiveManager, priorityModifier)
        {
            currSearchIndex = -1;
            this.equip = equip;
            this.itemIdentifiers = itemIdentifiers;
            for (int i = 0; i < itemIdentifiers.Length; i++)
            {
                itemIdentifiers[i] = itemIdentifiers[i].ToLowerInvariant();
            }
            if (checkInventory)
            {
                CheckInventory();
            }
        }

        private void CheckInventory()
        {
            if (itemIdentifiers == null) { return; }
            var item = character.Inventory.FindItem(i => itemIdentifiers.Any(id => i.Prefab.Identifier == id || i.HasTag(id)) && i.Condition > 0, recursive: true);
            if (item != null)
            {
                targetItem = item;
                moveToTarget = item.GetRootContainer() ?? item;
            }
        }

        protected override void Act(float deltaTime)
        {
            if (character.LockHands)
            {
                Abandon = true;
                return;
            }
            if (targetItem == null)
            {
                FindTargetItem();
                if (targetItem == null || moveToTarget == null)
                {
                    if (targetItem != null && moveToTarget == null)
                    {
#if DEBUG
                        DebugConsole.ThrowError($"{character.Name}: Move to target is null!");
#endif
                        Abandon = true;
                    }
                    objectiveManager.GetObjective<AIObjectiveIdle>().Wander(deltaTime);
                    return;
                }
            }
            if (character.IsItemTakenBySomeoneElse(targetItem))
            {
#if DEBUG
                DebugConsole.NewMessage($"{character.Name}: Found an item, but it's already equipped by someone else. Aborting.", Color.Yellow);
#endif
                Abandon = true;
            }
            if (character.CanInteractWith(targetItem, out _, checkLinked: false))
            {
                var pickable = targetItem.GetComponent<Pickable>();
                if (pickable == null)
                {
#if DEBUG
                    DebugConsole.NewMessage($"{character.Name}: Target not pickable. Aborting.", Color.Yellow);
#endif
                    Abandon = true;
                    return;
                }
                targetItem.TryInteract(character, forceSelectKey: true);
                if (equip)
                {
                    int targetSlot = -1;
                    //check if all the slots required by the item are free
                    foreach (InvSlotType slots in pickable.AllowedSlots)
                    {
                        if (slots.HasFlag(InvSlotType.Any)) { continue; }
                        for (int i = 0; i < character.Inventory.Items.Length; i++)
                        {
                            //slot not needed by the item, continue
                            if (!slots.HasFlag(character.Inventory.SlotTypes[i])) { continue; }
                            targetSlot = i;
                            //slot free, continue
                            var otherItem = character.Inventory.Items[i];
                            if (otherItem == null) { continue; }
                            //try to move the existing item to LimbSlot.Any and continue if successful
                            if (character.Inventory.TryPutItem(otherItem, character, new List<InvSlotType>() { InvSlotType.Any })) { continue; }
                            //if everything else fails, simply drop the existing item
                            otherItem.Drop(character);
                        }
                    }
                    if (character.Inventory.TryPutItem(targetItem, targetSlot, false, false, character))
                    {
                        IsCompleted = true;
                    }
                    else
                    {
#if DEBUG
                        DebugConsole.NewMessage($"{character.Name}: Failed to equip/move the item '{targetItem.Name}' into the character inventory. Aborting.", Color.Red);
#endif
                        Abandon = true;
                    }
                }
                else
                {
                    targetItem.ParentInventory.RemoveItem(targetItem);
                    if (character.Inventory.TryPutItem(targetItem, null, new List<InvSlotType>() { InvSlotType.Any }))
                    {
                        IsCompleted = true;
                    }
                    else
                    {
                        Abandon = true;
#if DEBUG
                        DebugConsole.NewMessage($"{character.Name}: Failed to equip/move the item '{targetItem.Name}' into the character inventory. Aborting.", Color.Red);
#endif
                        targetItem.Drop(character);
                    }
                }
            }
            else
            {
                TryAddSubObjective(ref goToObjective,
                    constructor: () =>
                    {
                        return new AIObjectiveGoTo(moveToTarget, character, objectiveManager, repeat: false, getDivingGearIfNeeded: AllowToFindDivingGear);
                    },
                    onAbandon: () =>
                    {
                        targetItem = null;
                        moveToTarget = null;
                        ignoredItems.Add(targetItem);
                        RemoveSubObjective(ref goToObjective);
                    },
                    onCompleted: () => RemoveSubObjective(ref goToObjective));
            }
        }

        /// <summary>
        /// searches for an item that matches the desired item and adds a goto subobjective if one is found
        /// </summary>
        private void FindTargetItem()
        {
            if (itemIdentifiers == null)
            {
                if (targetItem == null)
                {
#if DEBUG
                    DebugConsole.NewMessage($"{character.Name}: Cannot find the item, because neither identifiers nor item was defined.", Color.Red);
#endif
                    Abandon = true;
                }
                return;
            }
            for (int i = 0; i < 10 && currSearchIndex < Item.ItemList.Count - 1; i++)
            {
                currSearchIndex++;
                var item = Item.ItemList[currSearchIndex];
                if (ignoredItems.Contains(item)) { continue; }
                if (item.Submarine == null) { continue; }
                if (item.Submarine.TeamID != character.TeamID) { continue; }
                if (character.Submarine != null && !character.Submarine.IsEntityFoundOnThisSub(item, true)) { continue; }
                if (item.CurrentHull == null || item.Condition <= 0.0f) { continue; }
                if (itemIdentifiers.None(id => item.Prefab.Identifier == id || item.HasTag(id))) { continue; }
                if (ignoredContainerIdentifiers != null && item.Container != null)
                {
                    if (ignoredContainerIdentifiers.Contains(item.ContainerIdentifier)) { continue; }
                }
                if (character.IsItemTakenBySomeoneElse(item)) { continue; }
                float itemPriority = 1;
                if (GetItemPriority != null)
                {
                    itemPriority = GetItemPriority(item);
                }
                Item rootContainer = item.GetRootContainer();
                Vector2 itemPos = (rootContainer ?? item).WorldPosition;
                float yDist = Math.Abs(character.WorldPosition.Y - itemPos.Y);
                yDist = yDist > 100 ? yDist * 5 : 0;
                float dist = Math.Abs(character.WorldPosition.X - itemPos.X) + yDist;
                float distanceFactor = MathHelper.Lerp(1, 0, MathUtils.InverseLerp(0, 10000, dist));
                itemPriority *= distanceFactor;
                //ignore if the item has a lower priority than the currently selected one
                if (itemPriority < currItemPriority) { continue; }
                currItemPriority = itemPriority;
                targetItem = item;
                moveToTarget = rootContainer ?? item;
            }
            //if searched through all the items and a target wasn't found, can't be completed
            if (currSearchIndex >= Item.ItemList.Count - 1 && targetItem == null)
            {
#if DEBUG
                DebugConsole.NewMessage($"{character.Name}: Cannot find the item with the following identifier(s): {string.Join(", ", itemIdentifiers)}", Color.Yellow);
#endif
                Abandon = true;
            }
        }

        protected override bool Check()
        {
            if (IsCompleted) { return true; }
            if (targetItem != null)
            {
                return character.HasItem(targetItem, equip);
            }
            else if (itemIdentifiers != null)
            {
                var matchingItem = character.Inventory.FindItem(i => !ignoredItems.Contains(i) && itemIdentifiers.Any(id => id == i.Prefab.Identifier || i.HasTag(id)), recursive: true);
                if (matchingItem != null)
                {
                    return !equip || character.HasEquippedItem(matchingItem);
                }
                return false;
            }
            return false;
        }
    }
}
