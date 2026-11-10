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
        private static void RunTimerCommand(IMyTerminalBlock timerBlock)
        {
            var timer = timerBlock as IMyTimerBlock;
            if (timer == null)
                return;
            var ini = new MyIni();
            MyIniParseResult parseResult;
            if (!ini.TryParse(timer.CustomData ?? string.Empty, out parseResult))
            {
                Notify("HangarManager Custom Data is invalid: " + parseResult.Error, "Red");
                return;
            }

            var command = ini.Get(ConfigSection, "Command").ToString().Trim();
            var hangarName = ini.Get(ConfigSection, "Hangar").ToString().Trim();
            var requestId = ini.Get(ConfigSection, "RequestId").ToInt32(0);
            if (ini.Get(ResultSection, "RequestId").ToInt32(-1) == requestId
                && ini.Get(ResultSection, "Status").ToString() == "Pending"
                && ini.Get(RecoverySection, "Phase").ToString() != "Inspecting"
                && !PendingTimerCommands.ContainsKey(timer.EntityId))
            {
                RecoverTimerRequest(timer, ini);
                return;
            }
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(hangarName))
            {
                WriteTimerResult(timer, ini, command, "Error", "HangarManager requires Command and Hangar.", requestId);
                Notify("HangarManager requires Command and Hangar.", "Red");
                return;
            }

            var hangar = FindHangar(timer, hangarName);
            if (hangar == null)
            {
                WriteTimerResult(timer, ini, command, "Error", "Hangar not found: " + hangarName, requestId);
                Notify("Hangar not found: " + hangarName, "Red");
                return;
            }

            if (!hangar.HasPlayerAccess(MyAPIGateway.Session.Player?.IdentityId ?? 0))
            {
                WriteTimerResult(timer, ini, command, "Error", "Hangar access denied.", requestId);
                return;
            }
            if (PendingTimerCommands.Values.Any(p => p.Hangar != null && p.Hangar.EntityId == hangar.EntityId))
                return;

            if (command.Equals("Enable", StringComparison.OrdinalIgnoreCase) || command.Equals("On", StringComparison.OrdinalIgnoreCase))
            {
                AbandonAutomaticInspection(hangar);
                var enabled = SetHangarEnabled(hangar, true);
                WriteTimerResult(timer, ini, command, enabled ? "Success" : "Error", enabled ? "Hangar powered on." : "Hangar could not be powered on.", requestId);
                Notify(enabled ? "Hangar powered on." : "Hangar could not be powered on.", enabled ? "Green" : "Red");
                return;
            }

            if (command.Equals("Disable", StringComparison.OrdinalIgnoreCase) || command.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                AbandonAutomaticInspection(hangar);
                var disabled = SetHangarEnabled(hangar, false);
                WriteTimerResult(timer, ini, command, disabled ? "Success" : "Error", disabled ? "Hangar powered off." : "Hangar could not be powered off.", requestId);
                Notify(disabled ? "Hangar powered off." : "Hangar could not be powered off.", disabled ? "Green" : "Red");
                return;
            }

            if (command.Equals("RefreshPose", StringComparison.OrdinalIgnoreCase))
            {
                BeginTimerPoseRefresh(timer, ini, hangar, requestId);
                return;
            }

            if (command.Equals("RefreshContents", StringComparison.OrdinalIgnoreCase))
            {
                BeginTimerContentsRefresh(timer, ini, hangar, requestId);
                return;
            }

            if (command.Equals("Store", StringComparison.OrdinalIgnoreCase))
            {
                string storePoseError;
                if (!WriteHangarZonePose(timer, ini, hangar, requestId, out storePoseError))
                {
                    WriteTimerResult(timer, ini, command, "Error", "Hangar zone pose is unavailable: " + storePoseError, requestId);
                    return;
                }
                BeginTimerStore(timer, ini, hangar, requestId);
                return;
            }

            if (RequiresInspectionBeforeTimerCommand(command))
            {
                if (ini.Get(ResultSection, "RequestId").ToInt32() != requestId)
                    Inspected.Remove(hangar.EntityId);
                if (!EnsureInspected(hangar))
                {
                    WriteOperationJournal(timer, ini, hangar, command, requestId, "Inspecting", -1);
                    return;
                }
            }

            string poseError;
            if (!WriteHangarZonePose(timer, ini, hangar, requestId, out poseError))
            {
                if (IsTransientHangarInitializationError(poseError))
                {
                    Log("Hangar logic is not ready; keeping request " + requestId + " pending for retry.");
                    return;
                }

                WriteTimerResult(timer, ini, command, "Error", "Hangar zone pose is unavailable: " + poseError, requestId);
                Notify("Hangar zone pose is unavailable: " + poseError, "Red");
                return;
            }

            if (command.Equals("Deploy", StringComparison.OrdinalIgnoreCase) || command.Equals("Load", StringComparison.OrdinalIgnoreCase))
            {
                var slot = ini.Get(ConfigSection, "Slot").ToInt32(0);
                if (slot <= 0)
                    slot = 1;
                BeginTimerDeploy(timer, ini, hangar, requestId, slot);
                return;
            }

            WriteTimerResult(timer, ini, command, "Error", "Unknown HangarManager command: " + command, requestId);
            Notify("Unknown HangarManager command: " + command, "Red");
        }

        private static void BeginTimerPoseRefresh(IMyTimerBlock timer, MyIni ini, IMyTerminalBlock hangar, int requestId)
        {
            if (!BeginHangarPowerLease(hangar))
            {
                WriteTimerResult(timer, ini, "RefreshPose", "Error", "Hangar could not be powered on for pose refresh.", requestId);
                return;
            }

            string error;
            if (WriteHangarZonePose(timer, ini, hangar, requestId, out error))
            {
                bool restored = RestoreHangarPowerLease(hangar);
                WriteTimerResult(timer, ini, "RefreshPose", restored ? "Success" : "Error",
                    restored ? "Fresh hangar zone pose published." : "Fresh hangar zone pose published, but the original power state could not be restored.", requestId);
                return;
            }

            if (!IsTransientHangarInitializationError(error))
            {
                RestoreHangarPowerLease(hangar);
                WriteTimerResult(timer, ini, "RefreshPose", "Error", "Hangar zone pose is unavailable: " + error, requestId);
                return;
            }

            PendingTimerCommands[timer.EntityId] = new PendingTimerCommand
            {
                Timer = timer,
                Hangar = hangar,
                Command = "RefreshPose",
                RequestId = requestId,
                StartedTicks = SimulationTicks
            };
            WriteTimerResult(timer, ini, "RefreshPose", "Pending", "Waiting for Hangar mod initialization.", requestId);
            Log("RefreshPose request " + requestId + " is waiting for hangar logic initialization.");
        }

        private static void BeginTimerStore(IMyTimerBlock timer, MyIni ini, IMyTerminalBlock hangar, int requestId)
        {
            if (PendingTimerCommands.ContainsKey(timer.EntityId))
            {
                WriteTimerResult(timer, ini, "Store", "Pending", "Store request is already running.", requestId);
                return;
            }

            if (!BeginHangarPowerLease(hangar))
            {
                WriteTimerResult(timer, ini, "Store", "Error", "Hangar could not be powered on.", requestId);
                return;
            }

            object logic;
            string error;
            if (!TryGetHangarLogic(hangar, out logic, out error))
            {
                WriteTimerResult(timer, ini, "Store", "Error", error, requestId);
                RestoreHangarPowerLease(hangar);
                return;
            }

            int initialContents = GetHangarContentsCount(logic);
            if (initialContents < 0)
            {
                WriteTimerResult(timer, ini, "Store", "Error", "Hangar contents are unknown; Store was not invoked.", requestId);
                RestoreHangarPowerLease(hangar);
                Inspected.Remove(hangar.EntityId);
                return;
            }
            WriteOperationJournal(timer, ini, hangar, "Store", requestId, "Issued", initialContents);
            PublishInventory(hangar, -1);
            if (!InvokeButton(hangar, HangarStoreControlId))
            {
                WriteTimerResult(timer, ini, "Store", "Error", "Hangar Store is unavailable.", requestId);
                RestoreHangarPowerLease(hangar);
                return;
            }

            PendingTimerCommands[timer.EntityId] = new PendingTimerCommand
            {
                Timer = timer,
                Hangar = hangar,
                Logic = logic,
                Command = "Store",
                InitialContents = initialContents,
                RequestId = requestId,
                StartedTicks = SimulationTicks
            };
            WriteTimerResult(timer, ini, "Store", "Pending", "Waiting for Hangar mod confirmation.", requestId);
            Log("Store request started for " + hangar.CustomName + "; waiting for confirmation.");
        }

        private static void BeginTimerDeploy(IMyTimerBlock timer, MyIni ini, IMyTerminalBlock hangar, int requestId, int slot)
        {
            if (PendingTimerCommands.ContainsKey(timer.EntityId))
            {
                WriteTimerResult(timer, ini, "Deploy", "Pending", "Another hangar request is already running.", requestId);
                return;
            }

            if (!BeginHangarPowerLease(hangar))
            {
                WriteTimerResult(timer, ini, "Deploy", "Error", "Hangar could not be powered on.", requestId);
                return;
            }

            object logic;
            string error;
            if (!TryGetHangarLogic(hangar, out logic, out error))
            {
                WriteTimerResult(timer, ini, "Deploy", "Error", error, requestId);
                RestoreHangarPowerLease(hangar);
                return;
            }

            int initialContents = GetHangarContentsCount(logic);
            if (initialContents <= 0 || !SelectSlot(hangar, slot))
            {
                WriteTimerResult(timer, ini, "Deploy", "Error", "Stored ship slot is unavailable: " + slot, requestId);
                RestoreHangarPowerLease(hangar);
                return;
            }

            WriteOperationJournal(timer, ini, hangar, "Deploy", requestId, "Issued", initialContents);
            PublishInventory(hangar, -1);
            if (!InvokeButton(hangar, HangarLoadControlId))
            {
                WriteTimerResult(timer, ini, "Deploy", "Error", "Stored ship slot cannot be deployed: " + slot, requestId);
                RestoreHangarPowerLease(hangar);
                return;
            }

            PendingTimerCommands[timer.EntityId] = new PendingTimerCommand
            {
                Timer = timer,
                Hangar = hangar,
                Logic = logic,
                Command = "Deploy",
                InitialContents = initialContents,
                RequestId = requestId,
                StartedTicks = SimulationTicks
            };
            WriteTimerResult(timer, ini, "Deploy", "Pending", "Waiting for Hangar mod deployment confirmation.", requestId);
            Log("Deploy request started for " + hangar.CustomName + "; waiting for confirmation.");
        }

        private static void UpdatePendingTimerCommands()
        {
            if (PendingTimerCommands.Count == 0)
                return;
            var completed = new List<long>();
            foreach (var pair in PendingTimerCommands)
            {
                var pending = pair.Value;
                if (pending.Timer == null || pending.Hangar == null)
                {
                    if (pending?.Hangar != null)
                        RestoreHangarPowerLease(pending.Hangar);
                    completed.Add(pair.Key);
                    continue;
                }

                if (pending.Command.Equals("RefreshPose", StringComparison.OrdinalIgnoreCase))
                {
                    var ini = new MyIni();
                    MyIniParseResult result;
                    if (!ini.TryParse(pending.Timer.CustomData ?? string.Empty, out result)
                        || ini.Get(ConfigSection, "RequestId").ToInt32(0) != pending.RequestId)
                    {
                        RestoreHangarPowerLease(pending.Hangar);
                        completed.Add(pair.Key);
                        continue;
                    }

                    string error;
                    if (WriteHangarZonePose(pending.Timer, ini, pending.Hangar, pending.RequestId, out error))
                    {
                        bool restored = RestoreHangarPowerLease(pending.Hangar);
                        WriteTimerResult(pending.Timer, ini, "RefreshPose", restored ? "Success" : "Error",
                            restored ? "Fresh hangar zone pose published." : "Fresh hangar zone pose published, but the original power state could not be restored.", pending.RequestId);
                        completed.Add(pair.Key);
                        continue;
                    }

                    if (!IsTransientHangarInitializationError(error)
                        || SimulationTicks - pending.StartedTicks >= PoseRefreshTimeoutSeconds * 60)
                    {
                        RestoreHangarPowerLease(pending.Hangar);
                        WriteTimerResult(pending.Timer, ini, "RefreshPose", "Error", "Hangar zone pose is unavailable: " + error, pending.RequestId);
                        completed.Add(pair.Key);
                    }
                    continue;
                }

                if (pending.Logic == null)
                {
                    RestoreHangarPowerLease(pending.Hangar);
                    completed.Add(pair.Key);
                    continue;
                }

                double elapsed = SimulationTicks - pending.StartedTicks;
                bool requestPending = GetLogicField(pending.Logic, "RequestPending", false);
                string state = GetLogicField<object>(pending.Logic, "State", null)?.ToString() ?? "Unknown";
                int contents = GetHangarContentsCount(pending.Logic);
                bool isDeploy = string.Equals(pending.Command, "Deploy", StringComparison.OrdinalIgnoreCase);
                bool operationConfirmed = contents >= 0 && (isDeploy ? contents < pending.InitialContents : contents > pending.InitialContents);
                if (operationConfirmed)
                {
                    CompletePendingTimerCommand(pending, "Success", isDeploy ? "Hangar mod confirmed deployment." : "Hangar mod confirmed storage.");
                    completed.Add(pair.Key);
                    continue;
                }

                if (elapsed >= 30 * 60)
                {
                    CompletePendingTimerCommand(pending, "Error", "Timed out waiting for Hangar mod confirmation.");
                    completed.Add(pair.Key);
                    continue;
                }
                if (contents >= 0 && state.Equals("Idle", StringComparison.OrdinalIgnoreCase) && !requestPending)
                {
                    if (!pending.IdleSinceTicks.HasValue)
                        pending.IdleSinceTicks = SimulationTicks;
                    if (SimulationTicks - pending.IdleSinceTicks.Value >= 2 * 60)
                    {
                        string notification = GetLogicProperty(pending.Logic, "NotificationText") as string;
                        CompletePendingTimerCommand(
                            pending,
                            "Error",
                            string.IsNullOrWhiteSpace(notification) ? (isDeploy ? "Hangar returned idle without deploying the Miner." : "Hangar returned idle without storing the Miner.") : notification);
                        completed.Add(pair.Key);
                    }

                    continue;
                }

                pending.IdleSinceTicks = null;
            }

            foreach (var id in completed)
                PendingTimerCommands.Remove(id);
        }

        private static void CompletePendingTimerCommand(PendingTimerCommand pending, string status, string message)
        {
            var ini = new MyIni();
            MyIniParseResult result;
            ini.TryParse(pending.Timer.CustomData ?? string.Empty, out result);
            WriteTimerResult(pending.Timer, ini, pending.Command, status, message, pending.RequestId);
            PublishInventory(pending.Hangar, status.Equals("Success", StringComparison.OrdinalIgnoreCase) ? GetHangarContentsCount(pending.Logic) : -1);
            if (!status.Equals("Success", StringComparison.OrdinalIgnoreCase)) Inspected.Remove(pending.Hangar.EntityId);
            RestoreHangarPowerLease(pending.Hangar);
            Log(pending.Command + " result for " + pending.Hangar.CustomName + ": " + status + " - " + message);
        }

        private static void WriteTimerResult(IMyTimerBlock timer, MyIni ini, string command, string status, string message, int requestId)
        {
            ini.Set(ResultSection, "Command", command);
            ini.Set(ResultSection, "RequestId", requestId);
            ini.Set(ResultSection, "Status", status);
            ini.Set(ResultSection, "Message", message ?? string.Empty);
            timer.CustomData = ini.ToString();
        }
    }
}
