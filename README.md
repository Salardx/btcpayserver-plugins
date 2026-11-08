# Payment Watcher plugins for BTCPay Server

Watch-only payment methods for [BTCPay Server](https://btcpayserver.org): a store lists the addresses it controls, every invoice reserves one of them, and a listener records the transfers that arrive there over public JSON-RPC. Nothing else to run, no keys on the server, no custody. One shared core, one plugin per chain family:

| Plugin | Payment methods | Status |
| --- | --- | --- |
| `BTCPayServer.Plugins.Neo` | `GAS-NEO` (Neo N3), `GAS-NEOX` (Neo X) | preview |
| `BTCPayServer.Plugins.BNBChain` | `BNB-BSC`, `USDT-BSC`, `USDC-BSC` (BNB Smart Chain, BEP-20) | preview |
| Stellar | XLM and USDC on Stellar | planned |

**Status: preview.** Both plugins build against BTCPay Server 2.4.7, and in CI a real BTCPay Server takes invoices from creation to settlement on all three chains against fake nodes, including a restart. They have not yet carried a merchant's traffic. The chain layer comes from [neo-payment-watcher](https://github.com/Salardx/neo-payment-watcher), rewarded in COZ Proof of Working 2.16 and 2.17.

## What a store gets

- Payment methods with BTCPay's normal checkout: amount, address, QR code of a payment link (NEP-9 on Neo N3, EIP-681 on EVM chains, with the token's `transfer` call for tokens) or of the plain address.
- A pool of addresses per payment method. Each open invoice reserves one address until it is paid or its payment window closes, so two open invoices never share an address and a payment needs no customer input to match.
- A listener per chain that follows the blocks over JSON-RPC and looks only at reserved addresses: NEP-17 `Transfer` notifications on Neo N3; native transfers (block transactions plus receipts) and ERC-20 `Transfer` logs (by block hash) on EVM chains. On dBFT chains (Neo N3, Neo X) a block is final when it exists; on BNB Smart Chain the listener stays six blocks behind the tip. Either way a payment is settled the moment it is recorded.
- Rates through BTCPay's rate engine: GAS from Binance, BNB from Binance, USDT and USDC from Kraken; override them in the store's rate rules as usual. Partial payments and overpayments follow BTCPay's rules.
- Server settings to point each chain at a node of your own; by default the plugins use public nodes with failover.

Addresses are checked before they are accepted: Base58Check for Neo N3, and for EVM chains an EIP-55 checksum whenever the address is written in mixed case, so a mistyped address is refused rather than used.

## Install

Until the plugins are listed in the plugin directory, build and upload them (needs the .NET 10 SDK):

```sh
git clone --recurse-submodules https://github.com/Salardx/btcpayserver-plugins
cd btcpayserver-plugins
./pack.sh BTCPayServer.Plugins.Neo        # or BTCPayServer.Plugins.BNBChain
```

Upload the `.btcpay` file from `artifacts/packed/<plugin>/` under *Server Settings → Plugins*, restart, then in the store open *Wallets → Neo* or *Wallets → BNB Chain*, add addresses and enable the payment method. Requires BTCPay Server 2.4.7 or later. The CI workflow builds the same packages on every push and keeps them as artifacts.

## Configuration

| Where | Setting | Meaning |
| --- | --- | --- |
| Store → plugin → payment method | Addresses | The receiving addresses of this store. List several: each open invoice reserves one. |
| Store → plugin → payment method | Payment link format | NEP-9 / EIP-681 link (wallets that understand it prefill the amount) or address only. |
| Server → plugin → chain | JSON-RPC URL | The node to read blocks from. Empty means the public nodes. |
| Environment | `NEO_JSONRPC_URI`, `NEOX_JSONRPC_URI`, `BSC_JSONRPC_URI` | The same override from configuration or environment. |
| Environment | `BSC_USDT_CONTRACT`, `BSC_USDC_CONTRACT` | The token contract to watch. Mainnet has defaults (Binance-Peg BSC-USD and USDC, 18 decimals); the BNB testnet has no canonical ones, so the methods appear there only when set. |

BTCPay's own network type selects the networks: mainnet uses Neo N3 mainnet (magic 860833102), Neo X mainnet (chain 47763) and BNB Smart Chain mainnet (chain 56); testnet and regtest use Neo N3 testnet (894710606), Neo X testnet (chain 12227332) and the BNB Smart Chain testnet (chain 97).

## Repository layout

- `src/BTCPayServer.Plugins.Chains/`: the shared core. Address pools and reservations, the listener, payment recording, node health, the store and server pages, and under `Watcher/` the chain layer of neo-payment-watcher (JSON-RPC with failover, Neo N3 application-log parsing, EVM block, receipt and log parsing by block hash, address rules, payment links). Generic on the plugin type, so several plugins of this family run side by side.
- `src/BTCPayServer.Plugins.Neo/`, `src/BTCPayServer.Plugins.BNBChain/`: one plugin each: its chain definitions, two empty controllers that pick up the shared pages, its views and icon. Each is packed on its own and has its own entry in the plugin directory.
- `tests/BTCPayServer.Plugins.Tests/`: unit tests of the core and the definitions, and the host test, which starts BTCPay Server in-process with both plugins against a PostgreSQL database and fake Neo N3, Neo X and BNB Smart Chain nodes.
- Reservations and the listeners' cursors live in BTCPay's settings table under advisory locks, so two hosts never scan the same chain at once.

## Development

```sh
git clone --recurse-submodules https://github.com/Salardx/btcpayserver-plugins
dotnet build tests/BTCPayServer.Plugins.Tests -m:1 -p:StaticWebAssetsEnabled=false
dotnet test tests/BTCPayServer.Plugins.Tests -m:1 -p:StaticWebAssetsEnabled=false --no-build
```

The host test runs when `NEO_TEST_POSTGRES` holds a PostgreSQL connection string whose user may create databases; otherwise it is skipped. To run BTCPay Server from the submodule with the plugins loaded, `./plugin-register.sh` writes their debug paths into the submodule's `appsettings.dev.json`; start BTCPay Server with PostgreSQL available (`--chains none --nodefaultchain` is enough, no Bitcoin node is required).

## Limitations

- Native-coin payments sent *by a contract* (smart-contract wallets, some exchange withdrawals) are internal transfers and are not detected on EVM chains. Payments from an ordinary wallet are.
- NEO itself is not offered as a payment method: it is indivisible.
- No refunds or payouts: the plugins only watch.

## License

MIT.
