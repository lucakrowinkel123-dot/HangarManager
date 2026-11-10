using System;
namespace EmmaTiming
{
    // Ticks are 1/60 simulation second. Remote clients integrate the replicated
    // server rate; their own frame counter is deliberately never used.
    public sealed class SimulationClock
    {
        double ticks, lastWall, lastFrame, lastRatio;
        bool initialized, lastHost, lastPaused;
        public double Advance(bool host, double frame, double wall, double ratio, bool paused)
        {
            bool valid = !double.IsNaN(ratio) && !double.IsInfinity(ratio) && ratio >= 0;
            if (initialized && host == lastHost)
            {
                if (host) { if (!paused && frame >= lastFrame) ticks += frame - lastFrame; }
                else if (!paused && !lastPaused && valid && lastRatio >= 0 && wall >= lastWall)
                    ticks += (wall - lastWall) * lastRatio * 60;
            }
            initialized = true; lastHost = host; lastPaused = paused;
            lastFrame = frame; lastWall = wall; lastRatio = valid ? ratio : -1;
            return ticks;
        }
    }
}
