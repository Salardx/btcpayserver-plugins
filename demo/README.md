# Demo: BTCPay Server with the plugins, on testnets

Two pieces, usable together or apart. Nothing here touches mainnet: the payer scripts refuse it. The first part below is the NEAR plugin on the NEAR testnet; the other plugins follow.

- `DemoHost/`: a console program that runs BTCPay Server in-process with all eight plugins on a fresh PostgreSQL database, network testnet, no Bitcoin node. It prints an admin login and writes it to `demo-host.json`.
- `pay.mjs` and `record.mjs` (Node 20 or later): `pay.mjs` sends a testnet payment from a throwaway account the way a customer's wallet would (a NEAR transfer, or a NEP-141 `ft_transfer` with the invoice id as memo, registering the receiver first when needed); `record.mjs` drives a browser through the whole flow and records it.

```sh
# 1. BTCPay Server with the plugins (PostgreSQL must be reachable; the user may create databases)
DEMO_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres" dotnet run --project demo/DemoHost

# 2. in another shell: pay an invoice and record it
cd demo && npm ci && npx playwright install chromium
NEAR_SHOP=shop.testnet NEAR_PAYER_ID=payer.testnet NEAR_PAYER_KEY=ed25519:... node record.mjs
# add NEAR_USDC=3e2210e1184b45b64c8a434c0a7e7b23cc04ea7eb7a6c3c32520d03d4afcb8af for a second invoice in USDC (the payer needs test USDC from https://faucet.circle.com)
```

The recording lands in `demo/out/<time>/`: `demo.webm`, numbered screenshots (store settings, checkout, pay page, paid, invoice details) and `summary.json` with the invoice ids and transaction hashes. The same flow runs in GitHub Actions on demand (the `demo` job), with the accounts taken from repository secrets.

Testnet accounts are free: `near account create-account sponsor-by-faucet-service <name>.testnet autogenerate-new-keypair ... network-config testnet create` with [near-cli-rs](https://github.com/near/near-cli-rs), or any wallet switched to testnet. Keys belong in environment variables, never in this repository.

## The Solana plugin on devnet

`pay-solana.mjs` is a Solana Pay wallet in a script: it reads the checkout's link with the parser of the Solana Pay reference implementation (`@solana/pay`), builds the transfer with that implementation's `createTransfer`, which adds the invoice's reference to the transfer, signs with a throwaway devnet key, and afterwards checks the finished payment with the implementation's own `validateTransfer`. It refuses any cluster but devnet. `record-solana.mjs` opens two invoices on the store's one address, pays the second by its link, waits for the checkout to turn paid by itself, shows the invoice's payment list, and confirms the first invoice was left alone. Then it pays one more invoice the other way a customer has, on the plugin's pay page (in USDC when the payer's wallet holds test USDC, in SOL otherwise): it clicks the checkout's button, and on the page a wallet in the browser connects and is handed the transfer the server built. That wallet is `solana-test-wallet.mjs`, a Wallet Standard wallet the recorder puts into the browser the way an extension puts itself there; it passes the transaction to `pay-solana.mjs --transaction`, which signs and sends it only if it is the very transfer the reference implementation builds from the checkout's link (same program, accounts, amount and reference), and validates the finished payment as before. `SOLANA_DEMO_VIA=link` pays every invoice by the link, `SOLANA_DEMO_VIA=page` every one on the pay page.

`npm run smoke` runs the pay page's browser script outside a browser (`smoke-solana-paypage.mjs`): `node:vm` with a
stub page, a stub Wallet Standard wallet and a stub server, so what the script itself does is checked without a
browser, a network or a dependency. It needs nothing installed and takes a second.

```sh
# 1. as above
DEMO_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres" dotnet run --project demo/DemoHost

# 2. pay by Solana Pay and record it
SOLANA_SHOP=<wallet address> SOLANA_PAYER_KEY=<base58 keypair> node record-solana.mjs
```

Devnet SOL comes from https://faucet.solana.com, devnet USDC from https://faucet.circle.com. The shop's USDC token account is opened by the recorder (the payer pays for it), because a wallet that pays a Solana Pay request does not create it.

## The Arc, Avalanche, Flare, BNB Chain and IoTeX plugins on their testnets

One recorder for the five, `record-evm.mjs`, with `pay-evm.mjs` as the wallet: it signs and sends a plain transfer of the chain's coin, or a token `transfer`, from a throwaway testnet key (it checks the node's chain id and refuses the well-known mainnets). `EVM_PRESET` picks the plugin and the testnet:

| `EVM_PRESET` | Testnet | First invoice | Second invoice, when the payer holds the token |
| --- | --- | --- | --- |
| `arc` | Arc testnet (chain 5042002) | USDC, the chain's own coin | EURC |
| `avalanche` | Avalanche Fuji (chain 43113) | AVAX | USDC (Circle's test token) |
| `flare` | Flare Coston2 (chain 114) | 0.10 USD paid in FLR at the FTSO's price | FXRP (the test token) |
| `bnb` | BNB Smart Chain testnet (chain 97) | BNB | USDT (a test token; start DemoHost with `BSC_USDT_CONTRACT=0x337610d27c682E347C9cD60BD4b3b107C9d34dDd`) |
| `iotex` | IoTeX testnet (chain 4690) | IOTX | none: the testnet has no test ioUSDT |

```sh
# 1. as above
DEMO_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres" dotnet run --project demo/DemoHost

# 2. pay on the testnet and record it
EVM_PRESET=arc EVM_SHOP=0x... EVM_PAYER_KEY=0x... node record-evm.mjs
```

The recorder pays what the checkout asks for, so on Flare the amount in FLR is the oracle's conversion of the invoice's dollars. `summary.json` carries the explorer links, the rate, and the seconds from the mined transaction to the paid checkout. Test coins: https://faucet.circle.com (USDC and EURC on Arc, USDC on Fuji), https://faucet.flare.network (FLR and FXRP on Coston2), a Fuji faucet for AVAX, a BNB testnet faucet for BNB, https://faucet.iotex.io for IOTX on the IoTeX testnet.

In GitHub Actions the `demo` job records any of these when it is started with the input `demo` set to `solana`, `arc`, `avalanche`, `flare`, `bnb` or `iotex`.
