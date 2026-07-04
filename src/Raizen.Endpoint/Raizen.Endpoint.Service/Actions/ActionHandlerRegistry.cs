using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

public sealed class ActionHandlerRegistry
{
    private readonly Dictionary<ActionType, IActionHandler> _handlers;

    public ActionHandlerRegistry(IEnumerable<IActionHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.HandledType);
    }

    public IActionHandler? GetHandler(ActionType type)
        => _handlers.GetValueOrDefault(type);
}
