using VlmHub.Balancer.Api;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Server;

internal readonly record struct StatusUpdateResult(
    bool Changed,
    bool CameOnline,
    bool FirstOnline);

internal sealed class Runtime : IDisposable
{
    private readonly object _sync = new();
    private HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _blockedModels =
        new(StringComparer.OrdinalIgnoreCase);

    public VlmServerClient Client { get; }
    public SemaphoreSlim Slot { get; } = new(1, 1);

    public bool IsConfigured { get; private set; } = true;
    public bool IsOnline { get; private set; }
    public bool HasEverBeenOnline { get; private set; }
    public bool IsStuck { get; private set; }
    public string State { get; private set; } = "Desconocido";
    public string? ActiveModel { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? OfflineSince { get; private set; }
    public int ConsecutiveProbeFailures { get; private set; }
    public DateTimeOffset StateSince { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAssignedAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset CooldownUntil { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ModelsRefreshedAt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ModelsNextRefreshAt { get; private set; } = DateTimeOffset.MinValue;

    public string Name => Client.Definition.Name;
    public string ConfigurationKey { get; }

    public Runtime(VlmServerClient client)
    {
        Client = client;
        ConfigurationKey = BuildConfigurationKey(client.Definition);
    }

    public static string BuildConfigurationKey(ServerDefinition definition) =>
        $"{definition.Name.Trim()}|{definition.BuildBaseUri().AbsoluteUri.TrimEnd('/')}";

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

    public Status GetStatusSnapshot()
    {
        lock (_sync)
        {
            return new Status(State, ActiveModel, LastError, StateSince);
        }
    }

    public bool IsCurrentlyOnline()
    {
        lock (_sync)
        {
            return IsConfigured && IsOnline;
        }
    }

    public bool IsCurrentlyConfigured()
    {
        lock (_sync)
        {
            return IsConfigured;
        }
    }

    public void SetConfigured(bool configured)
    {
        lock (_sync)
        {
            var wasConfigured = IsConfigured;
            IsConfigured = configured;

            if (!configured)
            {
                CooldownUntil = DateTimeOffset.MaxValue;
                return;
            }

            CooldownUntil = DateTimeOffset.MinValue;
            ModelsNextRefreshAt = DateTimeOffset.MinValue;

            if (!wasConfigured)
            {
                // Un nodo reinsertado en la configuración debe demostrar que
                // sigue accesible antes de volver al scheduler.
                IsOnline = false;
                _models.Clear();
                ModelsRefreshedAt = DateTimeOffset.MinValue;
                _blockedModels.Clear();
            }
        }
    }

    public bool IsTemporarilyBusy()
    {
        lock (_sync)
        {
            return IsConfigured && IsOnline && !IsStuck && IsTransientState(State);
        }
    }

    public bool IsTemporarilyBusyForAny(IReadOnlyList<string> models)
    {
        lock (_sync)
        {
            if (!IsConfigured || !IsOnline || IsStuck || !IsTransientState(State))
            {
                return false;
            }

            return models.Any(SupportsModelUnsafe);
        }
    }

    public bool HasActiveModel(string model)
    {
        lock (_sync)
        {
            return string.Equals(ActiveModel, model, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool NeedsModelPreparation(string model)
    {
        lock (_sync)
        {
            if (IsErrorState(State))
            {
                return true;
            }

            return !State.Equals("Disponible", StringComparison.OrdinalIgnoreCase) ||
                   !string.Equals(ActiveModel, model, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool RequiresRecovery()
    {
        lock (_sync)
        {
            return IsErrorState(State);
        }
    }

    public DateTimeOffset GetLastAssignedAt()
    {
        lock (_sync)
        {
            return LastAssignedAt;
        }
    }

    public bool ShouldRefreshModels()
    {
        lock (_sync)
        {
            return IsConfigured && IsOnline && DateTimeOffset.UtcNow >= ModelsNextRefreshAt;
        }
    }

    public bool CanDispatch(string model)
    {
        lock (_sync)
        {
            return IsConfigured &&
                   IsOnline &&
                   !IsStuck &&
                   DateTimeOffset.UtcNow >= CooldownUntil &&
                   IsDispatchableState(State) &&
                   SupportsModelUnsafe(model) &&
                   !IsModelBlockedUnsafe(model) &&
                   Slot.CurrentCount > 0;
        }
    }

    public bool CanEventuallyServeAny(IReadOnlyList<string> models)
    {
        lock (_sync)
        {
            if (!IsConfigured || !IsOnline || IsStuck)
            {
                return false;
            }

            return models.Any(model => SupportsModelUnsafe(model) && !IsModelBlockedUnsafe(model));
        }
    }

    public StatusUpdateResult UpdateStatus(Status status, TimeSpan stuckTimeout)
    {
        lock (_sync)
        {
            var previousState = State;
            var previousModel = ActiveModel;
            var previousOnline = IsOnline;
            var previousStuck = IsStuck;
            var previousError = LastError;
            var now = DateTimeOffset.UtcNow;

            var firstOnline = !HasEverBeenOnline;
            var cameOnline = !previousOnline;

            if (!State.Equals(status.State, StringComparison.OrdinalIgnoreCase))
            {
                State = status.State;
                StateSince = status.LastStateChange ?? now;
            }
            else if (status.LastStateChange is { } reportedChange && reportedChange < StateSince)
            {
                StateSince = reportedChange;
            }

            ActiveModel = status.ActiveModel;
            LastError = status.LastError;
            LastSeenAt = now;
            IsOnline = true;
            HasEverBeenOnline = true;
            OfflineSince = null;
            ConsecutiveProbeFailures = 0;

            if (cameOnline)
            {
                // Un reinicio puede haber corregido el modelo que antes fallaba.
                // No conservamos penalizaciones antiguas después de reconectar.
                CooldownUntil = DateTimeOffset.MinValue;
                _blockedModels.Clear();
                _models.Clear();
                ModelsRefreshedAt = DateTimeOffset.MinValue;
                ModelsNextRefreshAt = DateTimeOffset.MinValue;
            }

            IsStuck = IsTransientState(State) && now - StateSince >= stuckTimeout;

            if (State.Equals("Disponible", StringComparison.OrdinalIgnoreCase) ||
                State.Equals("SinModelo", StringComparison.OrdinalIgnoreCase) ||
                IsErrorState(State))
            {
                IsStuck = false;
            }

            var changed =
                !previousState.Equals(State, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previousModel, ActiveModel, StringComparison.OrdinalIgnoreCase) ||
                previousOnline != IsOnline ||
                previousStuck != IsStuck ||
                !string.Equals(previousError, LastError, StringComparison.Ordinal);

            return new StatusUpdateResult(changed, cameOnline, firstOnline);
        }
    }

    public bool UpdateModels(IReadOnlySet<string> models, TimeSpan refreshInterval)
    {
        lock (_sync)
        {
            var changed = !_models.SetEquals(models);
            _models = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            ModelsRefreshedAt = now;
            ModelsNextRefreshAt = now + refreshInterval;
            return changed;
        }
    }

    public void DeferModelRefresh(TimeSpan retryDelay)
    {
        lock (_sync)
        {
            ModelsNextRefreshAt = DateTimeOffset.UtcNow + retryDelay;
        }
    }

    public bool MarkOffline(string error, TimeSpan cooldown)
    {
        lock (_sync)
        {
            var transitionedOffline = IsOnline;
            IsOnline = false;
            LastError = error;
            ConsecutiveProbeFailures++;
            OfflineSince ??= DateTimeOffset.UtcNow;
            CooldownUntil = DateTimeOffset.UtcNow + cooldown;
            return transitionedOffline || ConsecutiveProbeFailures == 1;
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

    public void MarkModelReady(string model)
    {
        lock (_sync)
        {
            ActiveModel = model;
            State = "Disponible";
            StateSince = DateTimeOffset.UtcNow;
            LastError = null;
            IsOnline = true;
            HasEverBeenOnline = true;
            IsStuck = false;
            OfflineSince = null;
            ConsecutiveProbeFailures = 0;
            CooldownUntil = DateTimeOffset.MinValue;
        }
    }

    public void MarkAssigned()
    {
        lock (_sync)
        {
            LastAssignedAt = DateTimeOffset.UtcNow;
        }
    }

    private bool SupportsModelUnsafe(string model)
    {
        if (_models.Contains(model))
        {
            return true;
        }

        // Después de un reinicio el catálogo se considera desconocido hasta
        // volver a consultar /api/model/list. Esto permite recuperar el nodo
        // aunque el listado falle temporalmente.
        return _models.Count == 0 && ModelsRefreshedAt == DateTimeOffset.MinValue;
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

    private static bool IsDispatchableState(string state) =>
        state.Equals("Disponible", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("SinModelo", StringComparison.OrdinalIgnoreCase) ||
        IsErrorState(state);

    private static bool IsTransientState(string state) =>
        state.Equals("Procesando", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("CargandoModelo", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("BajandoModelo", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Apagando", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorState(string state) =>
        state.Equals("ErrorModelo", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ErrorDaemon", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Client.Dispose();
        Slot.Dispose();
    }
}
