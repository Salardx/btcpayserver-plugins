namespace NeoPaymentWatcher.Services;

/// <summary>The three token identifiers the vendored chain layer refers to. In the watcher this is a full registry; the plugin only needs the constants.</summary>
public static class TokenRegistry
{
    public const string NeoHash = "0xef4073a0f2b305a38ec4050e4d3d28bc40ea63f5";
    public const string GasHash = "0xd2a4cff31913016155e38e474a2c06d08be276cf";

    /// <summary>The chain's own coin on an EVM network (GAS on Neo X), which has no contract.</summary>
    public const string Native = "native";
}
