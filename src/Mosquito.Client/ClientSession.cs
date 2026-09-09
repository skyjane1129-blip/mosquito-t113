using Mosquito.Client.Core;

namespace Mosquito.Client;

public enum SessionKind { None, Engineer, Cloud }

// A local engineer session grants device access, never cloud API credentials.
public sealed class ClientSession(CloudApiClient cloud)
{
    private CancellationTokenSource _lifetime = new();
    public SessionKind Kind { get; private set; }
    public string Account { get; private set; } = "";
    public int Generation { get; private set; }
    public CancellationToken Token => _lifetime.Token;
    public bool CanUseLocal => Kind != SessionKind.None;
    public bool CanUseCloud => Kind == SessionKind.Cloud && cloud.IsAuthenticated;
    public event Action? Changed;

    public void Set(SessionKind kind, string account = "")
    {
        var previous = _lifetime;
        _lifetime = new(); Generation++; Kind = kind; Account = account;
        previous.Cancel(); previous.Dispose();
        Changed?.Invoke();
    }
}
