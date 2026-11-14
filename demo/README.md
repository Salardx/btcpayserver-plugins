# Demo: BTCPay Server with the NEAR plugin, on the NEAR testnet

Two pieces, usable together or apart. Nothing here touches mainnet.

- `DemoHost/`: a console program that runs BTCPay Server in-process with the Neo, BNB Chain and NEAR plugins on a fresh PostgreSQL database, network testnet, no Bitcoin node. It prints an admin login and writes it to `demo-host.json`.
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

## The BNB Chain plugin on the BNB Smart Chain testnet

The same host, with `pay-evm.mjs` and `record-evm.mjs`. `pay-evm.mjs` signs and sends a plain transfer of the chain's coin, or a BEP-20 `transfer`, from a throwaway testnet key (chain 97 by default; it checks the node's chain id and refuses the well-known mainnets). `record-evm.mjs` opens the checkout, pays, waits for the checkout to turn paid by itself and shows the invoice's payment list; `summary.json` also carries the explorer links and the seconds from the mined transaction to the paid checkout.

```sh
# 1. as above; add BSC_USDT_CONTRACT=0x337610d27c682E347C9cD60BD4b3b107C9d34dDd for the USDT method (the testnet has no canonical USDT; this one is a test token)
DEMO_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres" dotnet run --project demo/DemoHost

# 2. pay a BNB invoice and record it; EVM_TOKEN adds a second invoice in the test token (the payer must hold it)
EVM_SHOP=0x... EVM_PAYER_KEY=0x... node record-evm.mjs
```

In GitHub Actions the `demo` job records this flow when it is started with the input `demo: bnb`.
