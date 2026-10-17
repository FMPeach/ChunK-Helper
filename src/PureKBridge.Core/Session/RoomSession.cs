namespace PureKBridge.Core.Session;

public enum ConnectionState
{
    Idle, Scanning, RoomDetected, WaitingForWechatAuthorization, ReadyToConnect,
    Connecting, WaitingForNotify, Connected, Disconnected, Error
}

public sealed class RoomSession
{
    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public event Action<ConnectionState>? StateChanged;

    public void TransitionTo(ConnectionState next)
    {
        if (next == State) return;
        var allowed = next == ConnectionState.Idle || next == ConnectionState.Error || State switch
        {
            ConnectionState.Idle => next is ConnectionState.Scanning or ConnectionState.RoomDetected,
            ConnectionState.Scanning => next == ConnectionState.RoomDetected,
            ConnectionState.RoomDetected => next is ConnectionState.Scanning or ConnectionState.WaitingForWechatAuthorization,
            ConnectionState.WaitingForWechatAuthorization => next is ConnectionState.ReadyToConnect or ConnectionState.RoomDetected,
            ConnectionState.ReadyToConnect => next is ConnectionState.Connecting or ConnectionState.WaitingForWechatAuthorization,
            ConnectionState.Connecting => next is ConnectionState.Connected or ConnectionState.WaitingForNotify or ConnectionState.ReadyToConnect or ConnectionState.Disconnected,
            ConnectionState.WaitingForNotify => next is ConnectionState.Connected or ConnectionState.Disconnected,
            ConnectionState.Connected => next is ConnectionState.Disconnected or ConnectionState.Connecting,
            ConnectionState.Disconnected => next is ConnectionState.Connecting or ConnectionState.ReadyToConnect,
            ConnectionState.Error => next is ConnectionState.Scanning or ConnectionState.RoomDetected or ConnectionState.ReadyToConnect or ConnectionState.Connecting or ConnectionState.WaitingForWechatAuthorization or ConnectionState.Disconnected,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"无法从 {State} 进入 {next}。");
        State = next;
        StateChanged?.Invoke(State);
    }
}
