// Patched by MajikTom: OnItemCraft and ConfirmPending track eligible queued tasks.
// Pulse and Service expire the initialized head timer; Rust delivers each unit.
// OnItemCraftCancelled, OnItemCraftFinished, ReleaseTask, OnPlayerDisconnected and Unload
// clear stale tracking. ValidateItemShortNames skips placeholders within either list.
using Newtonsoft.Json;
using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using System.Linq;
using System;

namespace Oxide.Plugins
{
    [Info("Instant Craft", "Vlad-0003 / Orange / rostov114", "3.0.1")]
    [Description("Allows players to instantly craft items with features")]
    public class InstantCraft : RustPlugin
    {
        #region Vars
        private const string permUse = "instantcraft.use";
        private const string permNormal = "instantcraft.normal";
        private readonly object False = false;
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permNormal, this);

            _config.Init(this);

            if (!_config.HasAnyBlockedItems)
            {
                Unsubscribe(nameof(CanCraft));
            }
        }

        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint blueprint, int amount, bool free)
        {
            if (_config.IsBlocked(blueprint.targetItem))
            {
                Message(itemCrafter.baseEntity, "Blocked");
                return False;
            }

            return null;
        }

        private object OnItemCraft(ItemCraftTask task, BasePlayer owner)
        {
            if (_unloaded || _config == null || task == null || owner == null || task.blueprint == null || task.blueprint.targetItem == null)
            {
                return null;
            }

            if (!owner.IsConnected || !HasInstantAccess(owner) || _config.IsBlocked(task.blueprint.targetItem))
            {
                return null;
            }

            if (_config.IsNormal(task.blueprint.targetItem))
            {
                Message(owner, "Normal");
                return null;
            }

            // Nominate only. Queue membership is confirmed on the next tick, after the stock queue add.
            _pending.Add(new Nomination(owner, task, task.taskUID));

            if (!_confirmQueued)
            {
                _confirmQueued = true;
                if (_confirmAction == null)
                {
                    _confirmAction = ConfirmPending;
                }
                NextTick(_confirmAction);
            }

            return null;
        }

        private void OnItemCraftCancelled(ItemCraftTask task, ItemCrafter crafter)
        {
            ReleaseTask(task, crafter);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            if (task == null || task.amount > 0)
            {
                // Partial completion: the task stays queued and tracked.
                return;
            }

            ReleaseTask(task, crafter);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null || (_active.Count == 0 && _pending.Count == 0))
            {
                return;
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Owner == player)
                {
                    _pending.RemoveAt(i);
                }
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i].Owner == player)
                {
                    RemoveActiveAt(i);
                }
            }
        }

        private void Unload()
        {
            // Invalidate deferred callbacks before releasing player and task references.
            _unloaded = true;

            if (_pulseTimer != null)
            {
                _pulseTimer.Destroy();
                _pulseTimer = null;
            }

            _pulseScheduled = false;
            _confirmQueued = false;
            _pending.Clear();
            _active.Clear();
            _cursor = 0;
            _confirmAction = null;
            _pulseAction = null;
        }

        #region Instant Queue
        private const float PulseSeconds = 0.05f;
        private const int MaxPerPulse = 32;
        private const int MaxConfirmPerTick = 32;

        private struct Nomination
        {
            public readonly BasePlayer Owner;
            public readonly ItemCraftTask Task;
            public readonly int Uid;
            public LinkedListNode<ItemCraftTask> Node;

            public Nomination(BasePlayer owner, ItemCraftTask task, int uid)
            {
                Owner = owner;
                Task = task;
                Uid = uid;
                Node = null;
            }
        }

        private readonly List<Nomination> _pending = new List<Nomination>();
        private readonly List<Nomination> _active = new List<Nomination>();
        private int _cursor;
        private bool _confirmQueued;
        private bool _pulseScheduled;
        private bool _unloaded;
        private Action _confirmAction;
        private Action _pulseAction;
        private Timer _pulseTimer;

        private bool IsEligibleItem(ItemCraftTask task)
        {
            var item = task.blueprint?.targetItem;
            return item != null && !_config.IsNormal(item) && !_config.IsBlocked(item);
        }

        private bool HasInstantAccess(BasePlayer owner)
        {
            string id = owner.UserIDString;
            return !permission.UserHasPermission(id, permNormal) && permission.UserHasPermission(id, permUse);
        }

        private static ItemCrafter GetCrafter(BasePlayer owner)
        {
            if (owner == null || owner.inventory == null)
            {
                return null;
            }

            ItemCrafter crafter = owner.inventory.crafting;
            return crafter == null || crafter.queue == null ? null : crafter;
        }

        private void ConfirmPending()
        {
            _confirmQueued = false;

            if (_unloaded)
            {
                _pending.Clear();
                return;
            }

            // Confirm against the actual queue after every plugin has returned.
            int count = Math.Min(MaxConfirmPerTick, _pending.Count);
            for (int i = 0; i < count; i++)
            {
                Nomination n = _pending[i];
                BasePlayer owner = n.Owner;
                ItemCraftTask task = n.Task;

                if (owner == null || !owner.IsConnected || task == null || task.blueprint == null)
                {
                    continue;
                }

                if (task.taskUID != n.Uid || task.amount <= 0 || !IsEligibleItem(task) || !HasInstantAccess(owner))
                {
                    continue;
                }

                ItemCrafter crafter = GetCrafter(owner);
                n.Node = crafter?.queue.Find(task);
                if (n.Node == null)
                {
                    continue;
                }

                _active.Add(n);
            }

            _pending.RemoveRange(0, count);
            if (_pending.Count > 0)
            {
                _confirmQueued = true;
                NextTick(_confirmAction);
            }
            EnsurePulse();
        }

        private void EnsurePulse()
        {
            if (_pulseScheduled || _unloaded || _active.Count == 0)
            {
                return;
            }

            if (_pulseAction == null)
            {
                _pulseAction = Pulse;
            }

            _pulseScheduled = true;
            _pulseTimer = timer.Once(PulseSeconds, _pulseAction);
        }

        private void Pulse()
        {
            _pulseScheduled = false;
            _pulseTimer = null;

            if (_unloaded)
            {
                return;
            }

            // Resume at the saved cursor so busy queues cannot starve later entries.
            int budget = Math.Min(MaxPerPulse, _active.Count);
            int idx = _cursor;

            while (budget-- > 0 && _active.Count > 0)
            {
                if (idx >= _active.Count)
                {
                    idx = 0;
                }

                if (Service(_active[idx]))
                {
                    idx++;
                }
                else
                {
                    _active.RemoveAt(idx);
                }
            }

            _cursor = idx >= _active.Count ? 0 : idx;
            EnsurePulse();
        }

        // Returns true to keep tracking, false to drop.
        private bool Service(Nomination n)
        {
            BasePlayer owner = n.Owner;
            ItemCraftTask task = n.Task;

            if (owner == null || !owner.IsConnected || task == null)
            {
                return false;
            }

            if (task.taskUID != n.Uid || task.amount <= 0 || !IsEligibleItem(task) || !HasInstantAccess(owner))
            {
                return false;
            }

            ItemCrafter crafter = GetCrafter(owner);
            if (crafter == null)
            {
                return false;
            }

            LinkedListNode<ItemCraftTask> head = crafter.queue.First;
            if (head == null || n.Node.List != crafter.queue || !ReferenceEquals(n.Node.Value, task))
            {
                return false;
            }

            if (head.Value != task)
            {
                // Not at the head yet: keep only while still queued.
                return true;
            }

            // Rust initializes zero endTime. Only expire an initialized head; Rust delivers each unit.
            float now = Time.realtimeSinceStartup;
            if (now > 0f && task.endTime > 0f && task.endTime > now)
            {
                task.endTime = now;
            }

            return true;
        }

        private void ReleaseTask(ItemCraftTask task, ItemCrafter crafter)
        {
            if (task == null)
            {
                return;
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pending[i].Task, task) && _pending[i].Uid == task.taskUID) _pending.RemoveAt(i);
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_active[i].Task, task) && _active[i].Uid == task.taskUID)
                {
                    RemoveActiveAt(i);
                }
            }
        }

        private void RemoveActiveAt(int index)
        {
            _active.RemoveAt(index);

            if (index < _cursor)
            {
                _cursor--;
            }

            if (_cursor >= _active.Count)
            {
                _cursor = 0;
            }
        }
        #endregion
        #endregion

        #region API

        [HookMethod(nameof(API_IsItemBlocked))]
        public bool API_IsItemBlocked(ItemDefinition itemDefinition)
        {
            return _config.IsBlocked(itemDefinition);
        }

        [HookMethod(nameof(API_GetIsItemBlockedCallback))]
        public Func<ItemDefinition, bool> API_GetIsItemBlockedCallback()
        {
            return itemDefinition => _config.IsBlocked(itemDefinition);
        }

        #endregion

        #region Localization 1.1.2
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"Blocked", "Crafting of that item is blocked!"},
                {"Normal", "Item will be crafted with normal speed."}
            }, this, "en");
        }

        public void Message(BasePlayer player, string messageKey, params object[] args)
        {
            if (player == null)
                return;

            player.ChatMessage(string.Format(lang.GetMessage(messageKey, this, player.UserIDString), args));
        }
        #endregion

        #region Configuration 3.0.0
        private Configuration _config;
        private class Configuration
        {
            [JsonProperty(PropertyName = "Normal Speed")]
            private string[] normal =
            {
                "hammer",
                "put item shortname here"
            };

            [JsonProperty(PropertyName = "Blacklist")]
            private string[] blocked =
            {
                "rock",
                "put item shortname here"
            };

            private HashSet<int> _normalCraftItemIds = new HashSet<int>();
            private HashSet<int> _blockedItemIds = new HashSet<int>();

            [JsonIgnore]
            public bool HasAnyBlockedItems => _blockedItemIds.Count > 0;

            public void Init(InstantCraft plugin)
            {
                ValidateItemShortNames(plugin, normal, _normalCraftItemIds);
                ValidateItemShortNames(plugin, blocked, _blockedItemIds);
            }

            public bool IsNormal(ItemDefinition itemDefinition) => _normalCraftItemIds.Contains(itemDefinition.itemid);
            public bool IsBlocked(ItemDefinition itemDefinition) => _blockedItemIds.Contains(itemDefinition.itemid);

            private void ValidateItemShortNames(InstantCraft plugin, string[] itemShortNameList, HashSet<int> validItemIdList)
            {
                foreach (var itemShortName in itemShortNameList)
                {
                    // A placeholder must not hide valid items later in the list.
                    if (string.IsNullOrEmpty(itemShortName) || itemShortName == "put item shortname here")
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                    if (itemDefinition == null)
                    {
                        plugin.PrintError($"Invalid item short name in config: {itemShortName}");
                        continue;
                    }

                    validItemIdList.Add(itemDefinition.itemid);
                }
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>();
                SaveConfig();
            }
            catch
            {
                PrintError("Error reading config, please check!");

                Unsubscribe(nameof(OnItemCraft));
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);
        #endregion
    }
}