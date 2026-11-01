using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

internal interface IAgentInteraction
{
    event Action? EndSessionRequested;
    void ShowMessage(string text, int durationMs);
    void SetLock(bool locked, string text);
    void ShowBroadcastFrame(byte[] jpeg);
    void HideBroadcast();
    void ShowChat(string from, string text, Func<string, Task> reply);
    void SetManagedState(bool active, string teacher, string classroom, bool assisting, bool recording);
}

internal interface ISessionRestrictions : IDisposable
{
    void Apply(RestrictCmd command);
    void Clear();
}

internal sealed record AgentOperations(Action<RemoteInputCmd, int> Input, Action<PowerCmd> Power, Action<RunCmd> Run)
{
    public static AgentOperations Default => new((command, _) => RemoteInput.Apply(command),
        command => PowerControl.Apply(command.Action), RunControl.Apply);
}
