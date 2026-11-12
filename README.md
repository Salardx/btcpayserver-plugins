# Payment Watcher plugins for BTCPay Server

Watch-only payment methods for [BTCPay Server](https://btcpayserver.org): a store lists the addresses it controls, every invoice reserves one of them, and a listener records the transfers that arrive there over public JSON-RPC. Nothing else to run, no keys on the server, no custody. One shared core, one plugin per chain family:

| Plugin | Payment methods | Status |
| --- | --- | --- |
| `BTCPayServer.Plugins.Neo` | `GAS-NEO` (Neo N3), `GAS-NEOX` (Neo X) | preview |
| `BTCPayServer.Plugins.BNBChain` | `BNB-BSC`, `USDT-BSC`, `USDC-BSC` (BNB Smart Chain, BEP-20) | preview |
| `BTCPayServer.Plugins.NEAR` | `NEAR-NEAR`, `USDC-NEAR`, `USDT-NEAR` (NEAR Protocol, NEP-141) | preview, built for the [NEAR Hackathon](#near-hackathon-entry) |

**Status: preview.** The plugins build against BTCPay Server 2.4.7, and in CI a real BTCPay Server takes invoices from creation to settlement on every chain against fake nodes, including a restart. They have not yet carried a merchant's traffic. The chain layer comes from [neo-payment-watcher](https://github.com/Salardx/neo-payment-watcher), rewarded in COZ Proof of Working 2.16 and 2.17.

## What a store gets

- Payment methods with BTCPay's normal checkout: amount, address, QR code of a payment link (NEP-9 on Neo N3, EIP-681 on EVM chains, with the token's `transfer` call for tokens) or of the plain address. On NEAR, which has no wallet link standard, the QR code and the "Pay in wallet" button open the plugin's **pay page**: the customer connects a NEAR wallet (MyNearWallet, Meteor, HOT, Nightly) and approves one transaction with the exact amount, the invoice id as the memo and, for a token, the store account's registration with the token contract when it is missing.
- A pool of addresses per payment method. Each open invoice reserves one address until it is paid or its payment window closes, so two open invoices never share an address and a payment needs no customer input to match.
- A listener per chain that looks only at reserved addresses. On Neo N3 and EVM chains it follows the blocks over JSON-RPC: NEP-17 `Transfer` notifications; native transfers (block transactions plus receipts) and ERC-20 `Transfer` logs by block hash. On NEAR, which makes a block every 0.6 seconds across a dozen shards, it works from **balances** instead: every few seconds it compares each reserved account's balance (NEAR, and each token through `ft_balance_of`) with the balance at the end of the previous turn, bisects the span of blocks when they differ until each change sits in one block, and reads that block's account changes and chunks to name the transaction behind the payment. Whatever path the money took (a wallet, a relayer paying the gas for a meta transaction, a contract, an intents settlement) the balance moved and the payment is recorded; the transaction hash is the extra when a wallet sent it directly, the receipt id otherwise. On dBFT chains (Neo N3, Neo X) a block is final when it exists; on BNB Smart Chain the listener stays six blocks behind the tip; on NEAR it reads the final block only. Either way a payment is settled the moment it is recorded.
- Rates through BTCPay's rate engine: GAS and BNB from Binance, NEAR from Coinbase, USDT and USDC from Kraken; override them in the store's rate rules as usual. Partial payments and overpayments follow BTCPay's rules.
- Server settings to point each chain at a node of your own; by default the plugins use public nodes with failover.

Addresses are checked before they are accepted: Base58Check for Neo N3; for EVM chains an EIP-55 checksum whenever the address is written in mixed case; on NEAR the account id rules, plus a question to the chain: a named account must exist (a transfer to a missing one bounces), and the store page says when an account is not yet registered with a token contract.

## Install

Until the plugins are listed in the plugin directory, build and upload them (needs the .NET 10 SDK):

```sh
git clone --recurse-submodules https://github.com/Salardx/btcpayserver-plugins
cd btcpayserver-plugins
./pack.sh BTCPayServer.Plugins.NEAR        # or BTCPayServer.Plugins.Neo, BTCPayServer.Plugins.BNBChain
```

Upload the `.btcpay` file from `artifacts/packed/<plugin>/` under *Server Settings → Plugins*, restart, then in the store open *Wallets → NEAR* (or *Neo*, *BNB Chain*), add accounts and enable the payment method. Requires BTCPay Server 2.4.7 or later. The CI workflow builds the same packages on every push and keeps them as artifacts; the Neo and BNB Chain packages are also on the [BTCPay plugin builder](https://plugin-builder.btcpayserver.org/public/plugins/neo-payments) as pre-releases.

## Configuration

| Where | Setting | Meaning |
| --- | --- | --- |
| Store → plugin → payment method | Addresses | The receiving addresses (accounts, on NEAR) of this store. List several: each open invoice reserves one. |
| Store → plugin → payment method | Payment link format | NEP-9 / EIP-681 link (wallets that understand it prefill the amount), the pay page on NEAR, or address only. |
| Server → plugin → chain | JSON-RPC URL | The node to read from. Empty means the public nodes. |
| Environment | `NEO_JSONRPC_URI`, `NEOX_JSONRPC_URI`, `BSC_JSONRPC_URI`, `NEAR_JSONRPC_URI` | The same override from configuration or environment. |
| Environment | `BSC_USDT_CONTRACT`, `BSC_USDC_CONTRACT`, `NEAR_USDT_CONTRACT`, `NEAR_USDC_CONTRACT` | The token contract to watch. Mainnet has defaults (Binance-Peg BSC-USD and USDC, 18 decimals; on NEAR Circle's USDC `17208628f84f5d6ad33f0da3bbbeb27ffcb398eac501a31bd6ad2011e36133a1` and Tether's `usdt.tether-token.near`, 6 decimals). The BNB testnet has no canonical ones, so the methods appear there only when set; the NEAR testnet has Circle's USDC (`3e2210e1184b45b64c8a434c0a7e7b23cc04ea7eb7a6c3c32520d03d4afcb8af`) built in and no USDT. |

BTCPay's own network type selects the networks: mainnet uses Neo N3 mainnet (magic 860833102), Neo X mainnet (chain 47763), BNB Smart Chain mainnet (chain 56) and NEAR mainnet; testnet and regtest use Neo N3 testnet (894710606), Neo X testnet (chain 12227332), the BNB Smart Chain testnet (chain 97) and NEAR testnet.

## Repository layout

- `src/BTCPayServer.Plugins.Chains/`: the shared core. Address pools and reservations, the listener, payment recording, node health, the store and server pages, and under `Watcher/` the chain layer of neo-payment-watcher (JSON-RPC with failover, Neo N3 application-log parsing, EVM block, receipt and log parsing by block hash, address rules, payment links). Under `Near/` the NEAR client: account id rules, the typed RPC calls, balance bisection, chunk and account-change parsing. Generic on the plugin type, so several plugins of this family run side by side.
- `src/BTCPayServer.Plugins.Neo/`, `src/BTCPayServer.Plugins.BNBChain/`, `src/BTCPayServer.Plugins.NEAR/`: one plugin each: its chain definitions, thin controllers that pick up the shared pages, its views and icon. The NEAR plugin adds the pay page (`Controllers/UINEARPayController.cs`, `Views/UINEARPay/Pay.cshtml`) and its browser script, bundled from `paypage/` into `Resources/nearpay/app.js`. Each plugin is packed on its own and has its own entry in the plugin directory.
- `tests/BTCPayServer.Plugins.Tests/`: unit tests of the core, the definitions and the NEAR client (against a fake NEAR node that serves balances at heights, chunks, account changes and skipped heights), and the host test, which starts BTCPay Server in-process with all plugins against a PostgreSQL database and fake Neo N3, Neo X, BNB Smart Chain and NEAR nodes.
- Reservations and the listeners' cursors live in BTCPay's settings table under advisory locks, so two hosts never scan the same chain at once.

## Development

```sh
git clone --recurse-submodules https://github.com/Salardx/btcpayserver-plugins
dotnet build tests/BTCPayServer.Plugins.Tests -m:1 -p:StaticWebAssetsEnabled=false
dotnet test tests/BTCPayServer.Plugins.Tests -m:1 -p:StaticWebAssetsEnabled=false --no-build
```

The host test runs when `NEO_TEST_POSTGRES` holds a PostgreSQL connection string whose user may create databases; otherwise it is skipped. `NEAR_LIVE=1` runs one read-only test of the NEAR client against the public testnet (it reads the plain transfers in the last blocks and checks that the balance scan finds every one of them). To run BTCPay Server from the submodule with the plugins loaded, `./plugin-register.sh` writes their debug paths into the submodule's `appsettings.dev.json`; start BTCPay Server with PostgreSQL available (`--chains none --nodefaultchain` is enough, no Bitcoin node is required). The pay page script is rebuilt with `cd src/BTCPayServer.Plugins.NEAR/paypage && npm ci && npm run build`.

## Limitations

- Native-coin payments sent *by a contract* (smart-contract wallets, some exchange withdrawals) are internal transfers and are not detected on EVM chains. Payments from an ordinary wallet are. NEAR has no such gap: the balance is the source of truth there.
- On NEAR the listener compares balances a few seconds apart. A payment that lands in the same few seconds as an outgoing transfer of exactly the same amount from the same account would cancel out and be missed; keep the receiving accounts for receiving. After more than about a day offline, a public node no longer serves the state at the start of the missed span; the listener logs it and watches from the present on (any invoice from before then is long past its monitoring window).
- NEO itself is not offered as a payment method: it is indivisible.
- No refunds or payouts: the plugins only watch.

## NEAR Hackathon entry

Built for the [NEAR Hackathon](https://nearhackathon.com) (development window 19 October to 14 November 2026), track Real-World Money: merchant payments in NEAR and USDC on NEAR for the thousands of self-hosted BTCPay Server stores, without a custodian or a payment processor in between. The customer pays from any NEAR wallet, the money lands in the store's own account, and the invoice settles seconds later.

**Baseline and what is new.** Everything up to commit [`fdcba68`](https://github.com/Salardx/btcpayserver-plugins/commit/fdcba68) (8 November 2026: the shared core, the Neo and BNB Chain plugins, the watcher's chain layer) is baseline, written for other purposes before this entry. The hackathon work is every commit after it, [in one diff](https://github.com/Salardx/btcpayserver-plugins/compare/fdcba68...main):

- `src/BTCPayServer.Plugins.NEAR/`: the plugin, the pay page and its wallet-selector bundle.
- `src/BTCPayServer.Plugins.Chains/Near/`: the NEAR client: balance-anchored detection with bisection, account changes and chunk reading, meta-transaction unwrapping, account existence and NEP-145 registration checks.
- In the core: range scanning and polling in the listener (`Services/IRangeScanner.cs`, `Services/ChainListener.cs`), destination checks (`Services/IDestinationChecker.cs`), the pay page link in the checkout, a NEAR-aware explorer link, named-parameter JSON-RPC with error causes.
- Tests: `NearScannerTests.cs`, `NEARTests.cs`, `NearLiveTests.cs`, `Host/FakeNearNode.cs`, and NEAR and USDC in the host test.

**Try it** (a judge's path, with the .NET 10 SDK and PostgreSQL):

1. `git clone --recurse-submodules https://github.com/Salardx/btcpayserver-plugins && cd btcpayserver-plugins`, then `dotnet test tests/BTCPayServer.Plugins.Tests -m:1 -p:StaticWebAssetsEnabled=false`: the NEAR client is exercised against the fake node (payments within and across shards, meta transactions, contract-sent transfers, tokens, skipped heights, refunds, throttling). With `NEO_TEST_POSTGRES` set, BTCPay Server itself starts and settles a NEAR and a USDC invoice; with `NEAR_LIVE=1`, the client reads the public testnet.
2. Run BTCPay Server with the plugin: `DEMO_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres" dotnet run --project demo/DemoHost` starts it on testnet with an admin login printed (see [demo/README.md](demo/README.md)). Create a store, open *Wallets → NEAR*, add a testnet account you control, enable `NEAR on NEAR (testnet)`.
3. Create an invoice, open the checkout, click *Pay in wallet*: the pay page opens. Connect a testnet wallet and approve, or send the amount to the account from any wallet or from `demo/pay.mjs`. The checkout turns paid within about ten seconds, and the invoice lists the payment with its transaction. `demo/record.mjs` does steps 2 and 3 by itself and records them.

The demo video was recorded by the `demo` job of the [GitHub Actions workflow](.github/workflows/build.yml): BTCPay Server in the runner, a real payment on the NEAR testnet, a browser recording. [Run 42611632686](https://github.com/Salardx/btcpayserver-plugins/actions/runs/42611632686) (9 November 2026) paid a 0.25 NEAR invoice with transaction `2EdTaxHuRh8oxZtZ7TY35qATbCF31YAe4GzPqyJE5yhS` and a 1 USDC invoice with `5PNtA26tZUrpMdyAQD5vnv1ZBKAq3p3PynKqmH2dzhRE`, both on testnet, and keeps the video and screenshots as its `near-demo` artifact. The test job runs the whole flow against fake nodes on every push. One person, one project: this repository is the only entry by its author.

## License

MIT.
