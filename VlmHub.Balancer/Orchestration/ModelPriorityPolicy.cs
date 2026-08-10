using VlmHub.Balancer.Models;
using VlmHub.Balancer.Server;

namespace VlmHub.Balancer.Orchestration;

internal static class ModelPriorityPolicy
{
    public static string? ResolveModel(
        ProcessingUnit unit,
        IReadOnlyList<string> priorities,
        ServerPool serverPool)
    {
        if (priorities.Count == 0)
        {
            return null;
        }

        // Avanza por prioridad sin gastar un intento cuando un modelo no está
        // registrado en ninguno de los servidores disponibles.
        for (var index = unit.ModelPriorityIndex; index < priorities.Count; index++)
        {
            if (serverPool.AnyOnlineSupports(priorities[index]))
            {
                unit.SkipUnavailablePriority(index);
                return priorities[index];
            }
        }

        // Si ya se consumió toda la lista de prioridad pero quedan intentos,
        // se reutiliza el último modelo de la lista que exista en el pool.
        for (var index = priorities.Count - 1; index >= 0; index--)
        {
            if (serverPool.AnyOnlineSupports(priorities[index]))
            {
                unit.SkipUnavailablePriority(index);
                return priorities[index];
            }
        }

        return null;
    }
}
