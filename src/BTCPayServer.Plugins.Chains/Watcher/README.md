# Vendored chain layer

These files are copied from [neo-payment-watcher](https://github.com/Salardx/neo-payment-watcher) (`src/NeoPaymentWatcher/Chains`, `Neo`), unchanged except that `ChainProfile.cs` keeps only the record (BTCPay Server supplies the chain parameters, so the preset resolver is not needed) and `TokenRegistry.cs` is reduced to its constants. `JsonRpcClient.cs` names `HttpClientJsonExtensions.PostAsJsonAsync` explicitly, because BTCPay Server ships an extension method with the same name in `System.Net.Http`.

They are the watch-only core: JSON-RPC with failover, Neo N3 block and application-log parsing, EVM block, receipt and log parsing by block hash, address checks (Base58 and EIP-55 with an in-house Keccak-256), amount maths and payment links. Sync them from the watcher repository when it changes; do not edit them here.

Two additions made for the NEAR plugin, to carry back to the watcher: `ChainProfile` knows a third kind, `near`; and `JsonRpcClient` accepts named parameters (an object instead of an array), reads a JSON-RPC error that arrives on a non-2xx status, and keeps NEAR's error `name` and `cause` on `JsonRpcException`. The NEAR chain client itself lives outside this folder, in `../Near/`.
