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
        private static Assembly FindCurrentHangarAssembly()
        {
            // Warm reloads leave retired mod assemblies in the process. Only the
            // current script registry identifies types belonging to this world.
            var scripts = Sandbox.Game.World.MyScriptManager.Static?.Scripts;
            return scripts?.Values.Distinct().Where(assembly => assembly.GetType("Hangar.HangarLogic", false) != null).SingleOrDefault();
        }

        private static bool TryGetHangarLogic(IMyTerminalBlock hangar, out object logic, out string error)
        {
            logic = null;
            error = string.Empty;
            try
            {
                var hangarAssembly = FindCurrentHangarAssembly();
                if (hangarAssembly == null)
                    throw new InvalidOperationException("Hangar mod assembly is not loaded");
                var extensionsType = hangarAssembly.GetType("Hangar.Extensions", false);
                var getLogic = extensionsType?.GetMethod("GetHangarLogic", BindingFlags.Public | BindingFlags.Static);
                logic = getLogic?.Invoke(null, new object[] { hangar as IMyCubeBlock });
                if (logic == null)
                    throw new InvalidOperationException("hangar logic is not ready");
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        private static bool IsTransientHangarInitializationError(string error)
        {
            string text = (error ?? string.Empty).ToLowerInvariant();
            return text.Contains("not ready") || text.Contains("not loaded") || text.Contains("not initialized") || text.Contains("api unavailable");
        }

        private static T GetLogicField<T>(object logic, string name, T fallback)
        {
            try
            {
                var field = logic.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = field?.GetValue(logic);
                return value is T ? (T)value : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static object GetLogicProperty(object logic, string name)
        {
            try
            {
                return logic.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(logic);
            }
            catch
            {
                return null;
            }
        }

        private static int GetHangarContentsCount(object logic)
        {
            var contents = GetLogicField<object>(logic, "Contents", null) as System.Collections.ICollection;
            return contents?.Count ?? -1;
        }

        private static bool WriteHangarZonePose(IMyTimerBlock timer, MyIni ini, IMyTerminalBlock hangar, int requestId, out string error)
        {
            error = string.Empty;
            try
            {
                object logic;
                if (!TryGetHangarLogic(hangar, out logic, out error))
                    throw new InvalidOperationException(error);
                var logicType = logic.GetType();
                var getCenter = logicType.GetMethod("GetHangarZoneCenterMatrix", BindingFlags.Instance | BindingFlags.NonPublic);
                var getZone = logicType.GetMethod("GetHangarZone", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(double) }, null);
                if (getCenter == null || getZone == null)
                    throw new MissingMethodException("the installed Hangar mod does not expose its zone geometry");
                var centerValue = getCenter.Invoke(logic, null);
                var zoneValue = getZone.Invoke(logic, new object[] { 0d });
                if (!(centerValue is MatrixD) || !(zoneValue is MyOrientedBoundingBoxD))
                    throw new InvalidOperationException("hangar zone geometry has not initialized");
                var matrix = (MatrixD)centerValue;
                var zone = (MyOrientedBoundingBoxD)zoneValue;
                ini.Set(PoseSection, "Status", "Ready");
                ini.Delete(PoseSection, "HangarEntityId");
                ini.Set(PoseSection, "RequestId", requestId);
                ini.Set(PoseSection, "Hangar", hangar.CustomName);
                ini.Set(PoseSection, "Center", VectorToString(matrix.Translation, 3));
                ini.Set(PoseSection, "Forward", VectorToString(matrix.Forward, 6));
                ini.Set(PoseSection, "Up", VectorToString(matrix.Up, 6));
                ini.Set(PoseSection, "HalfExtent", VectorToString(zone.HalfExtent, 3));
                ini.Delete(PoseSection, "Error");
                timer.CustomData = ini.ToString();
                Log("Published exact hangar zone pose for " + hangar.CustomName + ".");
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                ini.Set(PoseSection, "Status", "Error");
                ini.Delete(PoseSection, "HangarEntityId");
                ini.Set(PoseSection, "RequestId", requestId);
                ini.Set(PoseSection, "Hangar", hangar.CustomName);
                ini.Set(PoseSection, "Error", error);
                timer.CustomData = ini.ToString();
                Log("Could not publish hangar zone pose: " + error);
                return false;
            }
        }

        private static string VectorToString(Vector3D value, int decimals)
        {
            string format = "0." + new string('0', decimals);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return value.X.ToString(format, culture) + ":" + value.Y.ToString(format, culture) + ":" + value.Z.ToString(format, culture);
        }

        private static IMyTerminalBlock FindHangar(IMyTimerBlock timer, string name)
        {
            var terminalSystem = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(timer.CubeGrid);
            if (terminalSystem == null)
                return null;
            var blocks = new List<IMyTerminalBlock>();
            terminalSystem.GetBlocksOfType(blocks, block => IsHangar(block) && string.Equals(block.CustomName, name, StringComparison.OrdinalIgnoreCase));
            return blocks.FirstOrDefault();
        }

        private static bool SetHangarEnabled(IMyTerminalBlock hangar, bool enabled)
        {
            var functional = hangar as IMyFunctionalBlock;
            if (functional != null)
            {
                functional.Enabled = enabled;
                return functional.Enabled == enabled;
            }

            var action = hangar?.GetActionWithName(enabled ? "OnOff_On" : "OnOff_Off");
            if (action == null)
                return false;
            action.Apply(hangar);
            return true;
        }

        private static bool SelectSlot(IMyTerminalBlock hangar, int oneBasedSlot)
        {
            var controls = GetHangarControls(hangar);
            var selector = controls.FirstOrDefault(control => control.Id == HangarContentsControlId) as IMyTerminalControlCombobox;
            if (selector == null || selector.Setter == null)
                return false;
            selector.Setter(hangar, oneBasedSlot);
            return true;
        }

        private static void WriteHangarStatus(IMyTerminalBlock block, StringBuilder output)
        {
            output.Append(IsHangar(block) ? "Ready" : "Unavailable");
        }

        private static void WriteTimerStatus(IMyTerminalBlock block, StringBuilder output)
        {
            output.Append(IsTimer(block) ? "Custom Data" : "Unavailable");
        }

        private static void Notify(string message, string font)
        {
            MyAPIGateway.Utilities?.ShowNotification(message, 4000, font);
            Log(message);
        }

        private static void Log(string message)
        {
            System.Diagnostics.Trace.WriteLine("[HangarManager] " + message);
        }
    }
}
