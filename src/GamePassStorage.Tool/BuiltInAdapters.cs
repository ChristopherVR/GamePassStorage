using GamePassStorage.Adapters.AbioticFactor;

namespace GamePassStorage.Tool;

/// <summary>
/// The game adapters that ship inside the tool. To add a title, create
/// <c>src/GamePassStorage.Adapters.&lt;Game&gt;</c>, reference it from this project and list it here
/// (see docs/guide/adapter.md).
/// </summary>
internal static class BuiltInAdapters
{
    public static IReadOnlyList<IWgsGameAdapter> Create() =>
    [
        AbioticFactorAdapter.Instance,
    ];
}
