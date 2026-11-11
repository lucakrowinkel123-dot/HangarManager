using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;

namespace HangarManager
{
    public sealed partial class Plugin
    {
        private const string PowerSection = "HangarManagerPowerRecovery";
        private const string InventorySection = "HangarManagerInventory";
        private const string RecoverySection = "HangarManagerRecovery";
        private static readonly Dictionary<long, Inspection> Inspections = new Dictionary<long, Inspection>();
        private static readonly HashSet<long> Inspected = new HashSet<long>();
        private static readonly HashSet<long> InspectionFailed = new HashSet<long>();
        private sealed class Inspection
        {
            public IMyTerminalBlock Hangar;
            public object Logic;
            public object PreviousContents;
            public double Started;
            public int Requests;
            public double LastRequest;
            public IMyTimerBlock Timer;
            public int RequestId;
        }
        private static bool IsFinishedHangarResult(string status)
        {
            return string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsInterruptedOperationConfirmed(string command, int before, int current)
        {
            if (before < 0 || current < 0) return false;
            return command.Equals("Store", StringComparison.OrdinalIgnoreCase) ? current > before :
                command.Equals("Deploy", StringComparison.OrdinalIgnoreCase) && current < before;
        }
        private static bool BeginHangarPowerLease(IMyTerminalBlock hangar)
        {
            var block = hangar as IMyFunctionalBlock;
            if (block == null) return false;
            var ini = new MyIni();
            MyIniParseResult error;
            if (!ini.TryParse(hangar.CustomData ?? "", out error)) return false;
            if (!ini.ContainsKey(PowerSection, "OriginalEnabled"))
            {
                ini.Set(PowerSection, "OriginalEnabled", block.Enabled);
                hangar.CustomData = ini.ToString(); // Journal before changing power.
            }
            if (block.Enabled) return true;
            return SetHangarEnabled(hangar, true);
        }
        private static bool RestoreHangarPowerLease(IMyTerminalBlock hangar)
        {
            var ini = new MyIni();
            MyIniParseResult error;
            if (!ini.TryParse(hangar.CustomData ?? "", out error)) return false;
            if (!ini.ContainsKey(PowerSection, "OriginalEnabled")) return true;
            if (!SetHangarEnabled(hangar, ini.Get(PowerSection, "OriginalEnabled").ToBoolean())) return false;
            ini.DeleteSection(PowerSection);
            hangar.CustomData = ini.ToString();
            return true;
        }
        private static void PublishInventory(IMyTerminalBlock hangar, int count)
        {
            var ini = new MyIni();
            MyIniParseResult error;
            if (!ini.TryParse(hangar.CustomData ?? "", out error)) return;
            ini.Set(InventorySection, "Status", count < 0 ? "Unknown" : "Ready");
            ini.Set(InventorySection, "Count", count);
            ini.Set(InventorySection, "CheckedUtc", DateTime.UtcNow.ToString("o"));
            ini.Set(InventorySection, "Revision", Guid.NewGuid().ToString("N"));
            hangar.CustomData = ini.ToString();
        }
        private static bool EnsureInspected(IMyTerminalBlock hangar)
        {
            if (Inspected.Contains(hangar.EntityId)) return true;
            if (Inspections.ContainsKey(hangar.EntityId)) return false;
            var existing = new MyIni();
            MyIniParseResult existingError;
            if (existing.TryParse(hangar.CustomData ?? "", out existingError)
                && existing.ContainsKey(PowerSection, "OriginalEnabled")
                && !RestoreHangarPowerLease(hangar))
            {
                PublishInventory(hangar, -1);
                InspectionFailed.Add(hangar.EntityId);
                return false;
            }
            if (!BeginHangarPowerLease(hangar)) return false;
            PublishInventory(hangar, -1);
            Inspections[hangar.EntityId] = new Inspection { Hangar = hangar, Started = SimulationTicks };
            return false;
        }
        private static void BeginTimerContentsRefresh(IMyTimerBlock timer, MyIni ini, IMyTerminalBlock hangar, int requestId)
        {
            Inspection active;
            if (Inspections.TryGetValue(hangar.EntityId, out active)
                && active.Timer != null
                && active.Timer.EntityId == timer.EntityId
                && active.RequestId == requestId)
                return;

            Inspections.Remove(hangar.EntityId);
            Inspected.Remove(hangar.EntityId);
            InspectionFailed.Remove(hangar.EntityId);
            if (!BeginHangarPowerLease(hangar))
            {
                WriteTimerResult(timer, ini, "RefreshContents", "Error", "Hangar could not be powered on for contents refresh.", requestId);
                return;
            }

            PublishInventory(hangar, -1);
            Inspections[hangar.EntityId] = new Inspection
            {
                Hangar = hangar,
                Timer = timer,
                RequestId = requestId,
                Started = SimulationTicks
            };
            WriteOperationJournal(timer, ini, hangar, "RefreshContents", requestId, "Inspecting", -1);
            Log("RefreshContents request " + requestId + " started for " + hangar.CustomName + ".");
        }
        private static void CompleteContentsRefresh(Inspection scan, string status, string message)
        {
            if (scan.Timer == null) return;
            var ini = new MyIni();
            MyIniParseResult error;
            if (!ini.TryParse(scan.Timer.CustomData ?? string.Empty, out error)
                || ini.Get(ConfigSection, "RequestId").ToInt32(0) != scan.RequestId)
                return;
            WriteTimerResult(scan.Timer, ini, "RefreshContents", status, message, scan.RequestId);
        }
        private static bool RequiresInspectionBeforeTimerCommand(string command)
        {
            return command.Equals("Deploy", StringComparison.OrdinalIgnoreCase)
                || command.Equals("Load", StringComparison.OrdinalIgnoreCase);
        }
        private static void AbandonAutomaticInspection(IMyTerminalBlock hangar)
        {
            if (hangar == null) return;
            Inspections.Remove(hangar.EntityId);
            var ini = new MyIni();
            MyIniParseResult error;
            if (!ini.TryParse(hangar.CustomData ?? "", out error)
                || !ini.ContainsKey(PowerSection, "OriginalEnabled")) return;
            ini.DeleteSection(PowerSection);
            hangar.CustomData = ini.ToString();
        }
        private static bool ShouldStartAutomaticInspection(IMyTerminalBlock hangar)
        {
            return hangar != null && !hangar.Closed
                && !Inspected.Contains(hangar.EntityId)
                && !InspectionFailed.Contains(hangar.EntityId)
                && !Inspections.ContainsKey(hangar.EntityId);
        }
        private static void UpdateInspections()
        {
            var done = new List<long>();
            foreach (var pair in Inspections)
            {
                var scan = pair.Value;
                if (scan.Hangar == null || scan.Hangar.Closed) { done.Add(pair.Key); continue; }
                var current = scan.Logic == null ? null : GetLogicField<object>(scan.Logic, "Contents", null);
                int count = scan.Logic == null ? -1 : GetHangarContentsCount(scan.Logic);
                if (scan.Requests > 0 && current != null && !ReferenceEquals(current, scan.PreviousContents) && count >= 0)
                {
                    PublishInventory(scan.Hangar, count);
                    bool restored = RestoreHangarPowerLease(scan.Hangar);
                    if (restored)
                    {
                        Inspected.Add(pair.Key);
                        InspectionFailed.Remove(pair.Key);
                        done.Add(pair.Key);
                    }
                    CompleteContentsRefresh(scan, restored ? "Success" : "Error",
                        restored ? "Fresh hangar contents published: " + count + "." : "Fresh hangar contents published, but the original power state could not be restored.");
                }
                else if (SimulationTicks - scan.Started >= 30 * 60)
                {
                    PublishInventory(scan.Hangar, -1);
                    RestoreHangarPowerLease(scan.Hangar);
                    InspectionFailed.Add(pair.Key);
                    CompleteContentsRefresh(scan, "Error", "Server contents refresh timed out; occupancy remains unknown.");
                    done.Add(pair.Key);
                }
                else if (scan.Hangar.IsWorking && (scan.Requests == 0
                    || scan.Timer != null && scan.Requests == 1 && SimulationTicks - scan.LastRequest >= 6 * 60))
                    RequestHangarContents(scan);
            }
            foreach (var id in done) Inspections.Remove(id);
        }

        private static void RequestHangarContents(Inspection scan)
        {
            // The native server ignores contents requests while the block is not working.
            // A retry is the same read-only inspection and keeps the original list baseline.
            object logic;
            string error;
            if (!TryGetHangarLogic(scan.Hangar, out logic, out error)) return;
            if (scan.Requests > 0 && !ReferenceEquals(scan.Logic, logic)) return;
            var request = logic.GetType().Assembly.GetType("Hangar.Session", false)
                ?.GetMethod("GetSavedGrids", BindingFlags.Public | BindingFlags.Static);
            if (request == null) return;
            if (scan.Requests == 0)
            {
                scan.Logic = logic;
                scan.PreviousContents = GetLogicField<object>(logic, "Contents", null);
            }
            scan.Requests++;
            scan.LastRequest = SimulationTicks;
            try { request.Invoke(null, new object[] { scan.Hangar }); }
            catch (Exception ex) { Log("Contents refresh failed: " + ex.GetBaseException().Message); }
        }
        private static void RecoverTimerRequest(IMyTimerBlock timer, MyIni ini)
        {
            var command = ini.Get(ResultSection, "Command").ToString();
            var hangar = FindHangar(timer, ini.Get(ConfigSection, "Hangar").ToString());
            if (hangar != null && InspectionFailed.Contains(hangar.EntityId))
            {
                WriteTimerResult(timer, ini, command, "Error", "Server contents refresh timed out; occupancy remains unknown. No operation was repeated.",
                    ini.Get(ConfigSection, "RequestId").ToInt32());
                return;
            }
            if (hangar == null || !EnsureInspected(hangar)) return;
            object logic;
            string error;
            if (!TryGetHangarLogic(hangar, out logic, out error)) return;
            int count = GetHangarContentsCount(logic);
            if (count < 0)
            {
                Inspected.Remove(hangar.EntityId);
                PublishInventory(hangar, -1);
                return;
            }
            int requestId = ini.Get(ConfigSection, "RequestId").ToInt32();
            bool journalMatches = ini.Get(RecoverySection, "RequestId").ToInt32() == requestId
                && ini.Get(RecoverySection, "HangarEntityId").ToInt64() == hangar.EntityId;
            // Waiting for the initial scan is safe to resume: no button was invoked.
            if (journalMatches && ini.Get(RecoverySection, "Phase").ToString() == "Inspecting")
            {
                RunTimerCommand(timer);
                return;
            }
            int before = ini.Get(RecoverySection, "InitialContents").ToInt32(-1);
            bool confirmed = journalMatches && IsInterruptedOperationConfirmed(command, before, count);
            WriteTimerResult(timer, ini, command, confirmed ? "Success" : "Error",
                confirmed ? "Refreshed hangar contents confirm the interrupted operation." :
                "Interrupted operation could not be confirmed. Contents refreshed; command was not repeated.", requestId);
        }
        private static void WriteOperationJournal(IMyTimerBlock timer, MyIni ini, IMyTerminalBlock hangar,
            string command, int requestId, string phase, int initialCount)
        {
            ini.Set(RecoverySection, "RequestId", requestId);
            ini.Set(RecoverySection, "HangarEntityId", hangar.EntityId);
            ini.Set(RecoverySection, "Phase", phase);
            ini.Set(RecoverySection, "InitialContents", initialCount);
            WriteTimerResult(timer, ini, command, "Pending", "Waiting for confirmed hangar contents.", requestId);
        }
    }
}
