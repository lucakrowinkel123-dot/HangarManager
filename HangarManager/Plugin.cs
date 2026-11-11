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
        private const string StoreActionId = "HangarManager_Store";
        private const string DeployActionId = "HangarManager_DeploySelected";
        private const string ChooseDeployActionId = "HangarManager_ChooseDeploy";
        private const string TimerActionId = "HangarManager_RunCommand";
        private const string HangarStoreControlId = "Hangar_SaveButton";
        private const string HangarLoadControlId = "Hangar_LoadButton";
        private const string HangarContentsControlId = "Hangar_ContentsListbox";
        private const string ConfigSection = "HangarManager";
        private const string PoseSection = "HangarManagerPose";
        private const string ResultSection = "HangarManagerResult";
        private const int AutomaticCommandScanIntervalFrames = 60;
        private const int PoseRefreshTimeoutSeconds = 8;
        private static readonly Dictionary<long, PendingTimerCommand> PendingTimerCommands = new Dictionary<long, PendingTimerCommand>();
        private sealed class PendingTimerCommand
        {
            public IMyTimerBlock Timer;
            public IMyTerminalBlock Hangar;
            public object Logic;
            public string Command;
            public int InitialContents;
            public int RequestId;
            public double StartedTicks;
            public double? IdleSinceTicks;
        }

        private static readonly HashSet<string> HangarSubtypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sdx_hangar3x3",
            "sdx_hangar5x5Civilian",
            "sdx_hangar5x5Military",
            "sdx_hangar7x5",
            "sdx_hangar11x7",
            "sdx_hangar11x7Civilian"
        };
        private IMyTerminalAction storeAction;
        private IMyTerminalAction deployAction;
        private IMyTerminalAction chooseDeployAction;
        private IMyTerminalAction timerAction;
        private bool registered;
        private IMyTerminalControls registeredControls;
        private int automaticCommandScanFrames;
        private object currentSession;
        private static double SimulationTicks;
        public void Init(object gameInstance)
        {
        }

        public void Update()
        {
            EmmaTiming.GameSimulationTime.Poll();
            SimulationTicks = EmmaTiming.GameSimulationTime.Ticks;
            if (!ReferenceEquals(currentSession, MyAPIGateway.Session))
            {
                ResetTerminalActions();
                PendingTimerCommands.Clear();
                Inspections.Clear();
                Inspected.Clear();
                InspectionFailed.Clear();
                currentSession = MyAPIGateway.Session;
            }
            if (MyAPIGateway.Session == null || MyAPIGateway.TerminalControls == null)
            {
                ResetTerminalActions();
                PendingTimerCommands.Clear();
                Inspections.Clear();
                Inspected.Clear();
                return;
            }
            if (!ReferenceEquals(registeredControls, MyAPIGateway.TerminalControls))
                ResetTerminalActions();
            UpdateInspections();
            UpdatePendingTimerCommands();
            if (!registered)
                RegisterActions();
            automaticCommandScanFrames--;
            if (automaticCommandScanFrames <= 0)
            {
                automaticCommandScanFrames = AutomaticCommandScanIntervalFrames;
                ProcessAutomaticTimerCommands();
            }
        }

        public void Dispose()
        {
            ResetTerminalActions();
            foreach (var pending in PendingTimerCommands.Values)
                if (pending?.Hangar != null)
                    RestoreHangarPowerLease(pending.Hangar);
            foreach (var scan in Inspections.Values)
                if (scan.Hangar != null) RestoreHangarPowerLease(scan.Hangar);
            Inspections.Clear();
            Inspected.Clear();
            InspectionFailed.Clear();
            currentSession = null;
            PendingTimerCommands.Clear();
        }

        private static void ProcessAutomaticTimerCommands()
        {
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, entity => entity is IMyCubeGrid);
            foreach (var entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null)
                    continue;
                var terminalSystem = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
                if (terminalSystem == null)
                    continue;
                var timers = new List<IMyTimerBlock>();
                terminalSystem.GetBlocksOfType(
                    timers,
                    timer => timer != null && (timer.CustomData ?? string.Empty).IndexOf("[" + ConfigSection + "]", StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var timer in timers)
                {
                    var hangars = new List<IMyTerminalBlock>();
                    terminalSystem.GetBlocksOfType(hangars, block => IsHangar(block) && block.CubeGrid == timer.CubeGrid
                        && block.HasPlayerAccess(MyAPIGateway.Session.Player?.IdentityId ?? 0));
                    foreach (var hangar in hangars)
                        if (ShouldStartAutomaticInspection(hangar)) EnsureInspected(hangar);
                    ProcessAutomaticTimerCommand(timer);
                }
            }
        }

        private static void ProcessAutomaticTimerCommand(IMyTimerBlock timer)
        {
            var ini = new MyIni();
            MyIniParseResult parseResult;
            if (!ini.TryParse(timer.CustomData ?? string.Empty, out parseResult))
                return;
            int requestId = ini.Get(ConfigSection, "RequestId").ToInt32(0);
            if (requestId <= 0 && ini.Get(ResultSection, "Status").ToString() != "Pending")
                return;
            int completedRequestId = ini.Get(ResultSection, "RequestId").ToInt32(0);
            if (PendingTimerCommands.ContainsKey(timer.EntityId))
                return;
            if (completedRequestId == requestId)
            {
                if (!IsFinishedHangarResult(ini.Get(ResultSection, "Status").ToString()))
                    RecoverTimerRequest(timer, ini);
                return;
            }
            RunTimerCommand(timer);
        }
    }
}
