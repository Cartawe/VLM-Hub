using VlmHub.Balancer.Api;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Server;

internal sealed class Runtime : IDisposable
{
    private readonly object _sync = new();
    private HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _blockedModels =
        new(StringComparer.OrdinalIgnoreCase);

    public VlmServerClient Client { get; }
    public SemaphoreSlim Slot { get; } = new(1, 1);

    public bool IsOnline { get; private set; }
    public bool IsStuck { get; private set; }
    public string State { get; private set; } = "Desconocido";
    public string? ActiveModel { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset StateSince { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAssignedAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset CooldownUntil { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ModelsRefreshedAt { get; private set; } = DateTimeOffset.MinValue;

    public string Name => Client.Definition.Name;

    public Runtime(VlmServerClient client)
    {
        Client = client;
    }

    public IReadOnlySet<string> Models
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<string>(_models, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public bool IsCurrentlyOnline()
    {
        lock (_sync)
        {
            return IsOnline;
        }
    }

    public bool HasActiveModel(string model)
    {
        lock (_sync)
        {
            return string.Equals(ActiveModel, model, StringComparison.OrdinalIgnoreCase);
        }
    }

    public DateTimeOffset GetLastAssignedAt()
    {
        lock (_sync)
        {
            return LastAssignedAt;
        }
    }

    public DateTimeOffset GetModelsRefreshedAt()
    {
        lock (_sync)
        {
            return ModelsRefreshedAt;
        }
    }

    public bool IsOnlineAndSupports(string model)
    {
        lock (_sync)
        {
            return IsOnline &&
                   _models.Contains(model) &&
                   !IsModelBlockedUnsafe(model);
        }
    }

    public bool CanDispatch(string model)
    {
        lock (_sync)
        {
            return IsOnline &&
                   !IsStuck &&
                   DateTimeOffset.UtcNow >= CooldownUntil &&
                   (State.Equals("Disponible", StringComparison.OrdinalIgnoreCase) ||
                    State.Equals("SinModelo", StringComparison.OrdinalIgnoreCase) ||
                    State.Equals("ErrorModelo", StringComparison.OrdinalIgnoreCase)) &&
                   _models.Contains(model) &&
                   !IsModelBlockedUnsafe(model) &&
                   Slot.CurrentCount > 0;
        }
    }

    public void UpdateStatus(Status status, TimeSpan stuckTimeout)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;

            if (!State.Equals(status.State, StringComparison.OrdinalIgnoreCase))
            {
                State = status.State;
                StateSince = status.LastStateChange ?? now;
            }
            else if (status.LastStateChange is { } reportedChange && reportedChange < StateSince)
            {
                // El servidor puede haber entrado al estado antes de que VLMHub
                // comenzara a observarlo. Conservamos la fecha reportada por la API.
                StateSince = reportedChange;
            }

            ActiveModel = status.ActiveModel;
            LastError = status.LastError;
            LastSeenAt = now;
            IsOnline = true;

            var transientState =
                State.Equals("Procesando", StringComparison.OrdinalIgnoreCase) ||
                State.Equals("CargandoModelo", StringComparison.OrdinalIgnoreCase) ||
                State.Equals("BajandoModelo", StringComparison.OrdinalIgnoreCase) ||
                State.Equals("Apagando", StringComparison.OrdinalIgnoreCase);

            IsStuck = transientState && now - StateSince >= stuckTimeout;

            if (State.Equals("Disponible", StringComparison.OrdinalIgnoreCase) ||
                State.Equals("SinModelo", StringComparison.OrdinalIgnoreCase))
            {
                IsStuck = false;
            }
        }
    }

    public void UpdateModels(IReadOnlySet<string> models)
    {
        lock (_sync)
        {
            _models = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
            ModelsRefreshedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkOffline(string error, TimeSpan cooldown)
    {
        lock (_sync)
        {
            IsOnline = false;
            LastError = error;
            CooldownUntil = DateTimeOffset.UtcNow + cooldown;
        }
    }

    public void MarkTransientFailure(string error, TimeSpan cooldown)
    {
        lock (_sync)
        {
            LastError = error;
            CooldownUntil = DateTimeOffset.UtcNow + cooldown;
        }
    }

    public void BlockModel(string model, TimeSpan cooldown)
    {
        lock (_sync)
        {
            _blockedModels[model] = DateTimeOffset.UtcNow + cooldown;
        }
    }

    private bool IsModelBlockedUnsafe(string model)
    {
        if (!_blockedModels.TryGetValue(model, out var until))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow < until)
        {
            return true;
        }

        _blockedModels.Remove(model);
        return false;
    }

    public void MarkModelActive(string model)
    {
        lock (_sync)
        {
            ActiveModel = model;
        }
    }

    public void MarkAssigned()
    {
        lock (_sync)
        {
            LastAssignedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Slot.Dispose();
    }
}
