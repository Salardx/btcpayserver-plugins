# Vendored chain layer

These files are copied from [neo-payment-watcher](https://github.com/Salardx/neo-payment-watcher) (`src/NeoPaymentWatcher/Chains`, `Neo`), unchanged except that `ChainProfile.cs` keeps only the record (BTCPay Server supplies the chain parameters, so the preset resolver is not needed) and `TokenRegistry.cs` is reduced to its constants. `JsonRpcClient.cs` names `HttpClientJsonExtensions.PostAsJsonAsync` explicitly, because BTCPay Server ships an extension method with the same name in `System.Net.Http`.

They are the watch-only core: JSON-RPC with failover, Neo N3 block and application-log parsing, EVM block, receipt and log parsing by block hash, address checks (Base58 and EIP-55 with an in-house Keccak-256), amount maths and payment links. Sync them from the watcher repository when it changes; do not edit them here.
