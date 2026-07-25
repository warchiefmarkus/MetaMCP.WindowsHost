namespace MetaMCP.Host;

internal enum ComponentState
{
    Disabled,
    Starting,
    Online,
    Offline,
    Error,
}

internal enum OverallState
{
    Offline,
    Starting,
    Online,
    Degraded,
    Error,
}

internal sealed record RuntimeStatus(
    bool DesiredRunning,
    ComponentState Backend,
    ComponentState Frontend,
    ComponentState Database,
    ComponentState ReverseSsh,
    int? BackendPid,
    int? FrontendPid,
    string? LastError,
    DateTimeOffset UpdatedAt)
{
    public OverallState Overall
    {
        get
        {
            if (!DesiredRunning)
            {
                return OverallState.Offline;
            }

            if (Backend == ComponentState.Starting || Frontend == ComponentState.Starting)
            {
                return OverallState.Starting;
            }

            if (Backend != ComponentState.Online ||
                Frontend != ComponentState.Online ||
                Database != ComponentState.Online)
            {
                return LastError is null ? OverallState.Offline : OverallState.Error;
            }

            return ReverseSsh is ComponentState.Online or ComponentState.Disabled
                ? OverallState.Online
                : OverallState.Degraded;
        }
    }

    public static RuntimeStatus Stopped(bool sshEnabled) => new(
        false,
        ComponentState.Offline,
        ComponentState.Offline,
        ComponentState.Offline,
        sshEnabled ? ComponentState.Offline : ComponentState.Disabled,
        null,
        null,
        null,
        DateTimeOffset.Now);
}