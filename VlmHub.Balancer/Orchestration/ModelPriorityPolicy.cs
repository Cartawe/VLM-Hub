using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Orchestration;

/// <summary>
/// La infraestructura nunca modifica la prioridad. La unidad avanza de modelo
/// exclusivamente cuando recibe finish_reason=error. stop y length resuelven
/// la unidad sin nuevos intentos.
/// </summary>
internal static class ModelPriorityPolicy
{
    public static string? ResolveModel(
        ProcessingUnit unit,
        IReadOnlyList<string> priorities)
    {
        if (priorities.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp(unit.ModelPriorityIndex, 0, priorities.Count - 1);
        return priorities[index];
    }
}
