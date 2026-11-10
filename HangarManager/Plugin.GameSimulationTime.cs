using System.Diagnostics;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
namespace EmmaTiming
{
    public static class GameSimulationTime
    {
        static object session;
        static SimulationClock clock = new SimulationClock();
        public static double Ticks { get; private set; }
        // Called every client update, even when the display refreshes only once a second.
        public static void Poll()
        {
            var current = MySession.Static;
            if (!ReferenceEquals(session, current))
            { session = current; clock = new SimulationClock(); Ticks = 0; }
            if (current == null) return;
            double ratio = MyAPIGateway.Physics == null ? double.NaN : MyAPIGateway.Physics.ServerSimulationRatio;
            Ticks = clock.Advance(Sync.IsServer, current.GameplayFrameCounter,
                (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency, ratio, MySandboxGame.IsPaused);
        }
    }
}
