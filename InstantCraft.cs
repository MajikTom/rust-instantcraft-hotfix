using Newtonsoft.Json;
using System.Collections.Generic;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using System.Linq;
using System;

namespace Oxide.Plugins
{
    [Info("Instant Craft", "Vlad-0003 / Orange / rostov114", "3.0.0")]
    [Description("Allows players to instantly craft items with features")]
    public class InstantCraft : RustPlugin
    {
        #region Vars
        private const string permUse = "instantcraft.use";
        private const string permNormal = "instantcraft.normal";
        private readonly object True = true;
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
            if (task.cancelled)
            {
                return null;
            }

            if (permission.UserHasPermission(owner.UserIDString, permNormal) || !permission.UserHasPermission(owner.UserIDString, permUse))
            {
                return null;
            }

            if (_config.IsNormal(task.blueprint.targetItem))
            {
                Message(owner, "Normal");
                return null;
            }

            int amountToCreate = task.blueprint.amountToCreate;
            task.blueprint.amountToCreate = task.amount * amountToCreate;
            task.workbenchEntity = owner.GetCachedCraftLevelWorkbench();
            owner.inventory.crafting.FinishCrafting(task);
            task.blueprint.amountToCreate = amountToCreate;

            return True;
        }
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
                    if (string.IsNullOrEmpty(itemShortName) || itemShortName == "put item shortname here")
                        return;

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