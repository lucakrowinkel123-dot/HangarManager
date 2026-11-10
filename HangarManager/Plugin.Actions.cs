using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.ModAPI;
using VRage.Plugins;
using VRageMath;

namespace HangarManager
{
    public sealed partial class Plugin : IPlugin
    {
        private void RegisterActions()
        {
            storeAction = CreateAction(StoreActionId, "Store Ship", StoreShip, IsHangar, WriteHangarStatus);
            deployAction = CreateAction(DeployActionId, "Deploy First", DeployFirst, IsHangar, WriteHangarStatus);
            chooseDeployAction = CreateAction(ChooseDeployActionId, "Choose Ship to Deploy", ShowDeployMenu, IsHangar, WriteHangarStatus);
            timerAction = CreateAction(TimerActionId, "Run HangarManager Command", RunTimerCommand, IsTimer, WriteTimerStatus);
            MyAPIGateway.TerminalControls.CustomActionGetter += AddActions;
            registered = true;
            Log("HangarManager loaded.");
        }

        private static IMyTerminalAction CreateAction(
            string id,
            string name,
            Action<IMyTerminalBlock> action,
            Func<IMyTerminalBlock, bool> enabled,
            Action<IMyTerminalBlock, StringBuilder> writer)
        {
            var terminalAction = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            terminalAction.Name = new StringBuilder(name);
            terminalAction.Icon = @"Textures\GUI\Icons\Actions\SendSignal.dds";
            terminalAction.ValidForGroups = false;
            terminalAction.Enabled = enabled;
            terminalAction.Action = action;
            terminalAction.Writer = writer;
            return terminalAction;
        }

        private void AddActions(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            if (IsHangar(block))
            {
                AddOnce(actions, storeAction);
                AddOnce(actions, deployAction);
                AddOnce(actions, chooseDeployAction);
            }
            else if (IsTimer(block))
            {
                AddOnce(actions, timerAction);
            }
        }

        private static void AddOnce(List<IMyTerminalAction> actions, IMyTerminalAction action)
        {
            if (action != null && !actions.Contains(action))
                actions.Add(action);
        }

        private static bool IsHangar(IMyTerminalBlock block)
        {
            return block != null && HangarSubtypes.Contains(block.BlockDefinition.SubtypeName);
        }

        private static bool IsTimer(IMyTerminalBlock block)
        {
            return block is IMyTimerBlock;
        }

        private static void StoreShip(IMyTerminalBlock hangar)
        {
            if (!SetHangarEnabled(hangar, true))
            {
                Notify("Hangar could not be powered on.", "Red");
                return;
            }

            if (!InvokeButton(hangar, HangarStoreControlId))
            {
                Notify("Hangar Store is unavailable.", "Red");
                return;
            }

            if (SetHangarEnabled(hangar, false))
                Notify("Store request sent; hangar powered off.", "Green");
            else
                Notify("Store request sent, but the hangar could not be powered off.", "Red");
        }

        private static void DeployFirst(IMyTerminalBlock hangar)
        {
            if (!SetHangarEnabled(hangar, true))
            {
                Notify("Hangar could not be powered on.", "Red");
                return;
            }

            if (!SelectSlot(hangar, 1))
            {
                Notify("No stored ships are available.", "Red");
                return;
            }

            if (InvokeButton(hangar, HangarLoadControlId))
                Notify("Deploy request sent.", "Green");
            else
                Notify("The first stored ship cannot be deployed right now.", "Red");
        }

        private static void ShowDeployMenu(IMyTerminalBlock hangar)
        {
            var controls = GetHangarControls(hangar);
            var selector = controls.FirstOrDefault(control => control.Id == HangarContentsControlId) as IMyTerminalControlCombobox;
            if (selector?.ComboBoxContent == null)
            {
                Notify("Hangar contents are unavailable.", "Red");
                return;
            }

            var items = new List<MyTerminalControlComboBoxItem>();
            selector.ComboBoxContent(items);
            var choices = items.Where(item => item.Key > 0).Select(item => new HangarChoice(item.Key, item.Value.ToString())).ToList();
            if (choices.Count == 0)
            {
                Notify("No stored ships are available.", "Red");
                return;
            }

            MyGuiSandbox.AddScreen(new HangarSelectionScreen(hangar.CustomName, choices, slot =>
            {
                if (SelectSlot(hangar, (int)slot) && InvokeButton(hangar, HangarLoadControlId))
                    Notify("Deploy request sent.", "Green");
                else
                    Notify("That stored ship cannot be deployed right now.", "Red");
            }));
        }

        private static bool InvokeButton(IMyTerminalBlock block, string controlId)
        {
            var controls = GetHangarControls(block);
            var button = controls.FirstOrDefault(control => control.Id == controlId) as IMyTerminalControlButton;
            if (button == null || (button.Visible != null && !button.Visible(block)) || (button.Enabled != null && !button.Enabled(block)))
                return false;
            button.Action(block);
            return true;
        }

        private static List<IMyTerminalControl> GetHangarControls(IMyTerminalBlock block)
        {
            var controls = new List<IMyTerminalControl>();
            MyAPIGateway.TerminalControls.GetControls<IMyTerminalBlock>(out controls);
            if (controls.Any(control => control.Id == HangarStoreControlId))
                return controls;
            // The hangar mod creates its controls only when its terminal getter first runs.
            // Initialize that getter so toolbar and timer commands also work before the
            // player has manually opened the hangar's terminal page.
            try
            {
                var terminalControlsType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Hangar.TerminalControls", false)).FirstOrDefault(type => type != null);
                var getter = terminalControlsType?.GetMethod("ControlGetter", BindingFlags.Public | BindingFlags.Static);
                getter?.Invoke(null, new object[] { block, controls });
            }
            catch (Exception exception)
            {
                Log("Could not initialize hangar controls: " + exception.Message);
            }

            return controls;
        }
    }
}
