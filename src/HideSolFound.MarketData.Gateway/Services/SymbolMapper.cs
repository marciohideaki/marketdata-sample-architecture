using System.Collections.Frozen;

namespace HideSolFound.MarketData.Gateway.Services;

/// <summary>
/// Bidirectional mapping between numeric symbol indices (used on the hot path)
/// and human-readable symbol names (used in APIs and persistence).
/// </summary>
public sealed class SymbolMapper
{
    private readonly FrozenDictionary<int, string> _indexToName;
    private readonly FrozenDictionary<string, int> _nameToIndex;

    public SymbolMapper()
    {
        var mapping = new Dictionary<int, string>
        {
            [0] = "PETR4",
            [1] = "VALE3",
            [2] = "ITUB4",
            [3] = "BBDC4",
            [4] = "ABEV3",
            [5] = "B3SA3",
        };

        _indexToName = mapping.ToFrozenDictionary();
        _nameToIndex = mapping.ToFrozenDictionary(kvp => kvp.Value, kvp => kvp.Key);
    }

    public string? GetSymbolName(int index) =>
        _indexToName.TryGetValue(index, out string? name) ? name : null;

    public int? GetSymbolIndex(string name) =>
        _nameToIndex.TryGetValue(name, out int index) ? index : null;

    public IEnumerable<string> AllSymbols => _indexToName.Values;
}
