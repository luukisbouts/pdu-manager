namespace PduManager.Services;

/// <summary>
/// Decides when an outlet with "Auto Power-Off" enabled must be switched off because its monitored device went down.
/// Safety rules: the device must first have been seen UP (so a device that is still booting is never cut),
/// and must then fail <see cref="FailureThreshold"/> consecutive pings (one ping per 5 s poll) before power is cut.
/// One instance per PDU. Call <see cref="Evaluate"/> once per outlet after every poll.
/// </summary>
public class AutoPowerOffMonitor
{
    public const int FailureThreshold = 6;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private sealed class State
    {
        public bool SeenUp;
        public int Failures;
        public DateTime? LastFired;
    }

    private readonly Dictionary<int, State> _states = new();
    private readonly object _sync = new();

    /// <summary>True when the outlet should be switched off now (repeated every 30 s until the PDU reports it OFF).</summary>
    public bool Evaluate(int outlet, bool enabled, bool hasMonitorIp, bool? outletOn, bool pduOnline, bool? deviceUp)
    {
        lock (_sync)
        {
            // Nothing to protect: forget everything (a later power-on has to re-arm by answering pings first).
            if (!enabled || !hasMonitorIp || outletOn == false)
            {
                _states.Remove(outlet);
                return false;
            }
            if (outletOn is null || !pduOnline || deviceUp is null) return false; // unknown: neither arm nor count

            if (!_states.TryGetValue(outlet, out var s)) _states[outlet] = s = new State();

            if (deviceUp == true)
            {
                s.SeenUp = true;
                s.Failures = 0;
                s.LastFired = null;
                return false;
            }

            if (!s.SeenUp) return false;
            if (++s.Failures < FailureThreshold) return false;

            var now = DateTime.UtcNow;
            if (s.LastFired is { } last && now - last < RetryAfter) return false;
            s.LastFired = now;
            return true;
        }
    }

    /// <summary>Forget what was learned about an outlet (e.g. its monitor IP changed).</summary>
    public void Reset(int outlet)
    {
        lock (_sync) _states.Remove(outlet);
    }

    /// <summary>True once the device was seen up, i.e. the next failed pings will lead to a power-off.</summary>
    public bool IsArmed(int outlet)
    {
        lock (_sync) return _states.TryGetValue(outlet, out var s) && s.SeenUp;
    }
}
