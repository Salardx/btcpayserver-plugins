// Pays a BTCPay invoice on the NEAR testnet the way a customer's wallet would, from a throwaway account:
//
//   node pay.mjs --to shop.testnet --amount 1.5                                     NEAR, a plain transfer
//   node pay.mjs --to shop.testnet --amount 10 --token <contract> --memo <invoice>   a NEP-141 ft_transfer; registers the receiver first if needed
//
// The payer comes from the environment: NEAR_PAYER_ID and NEAR_PAYER_KEY (an "ed25519:..." secret key). Never commit either.
// Optional: NEAR_NETWORK (testnet), NEAR_RPC (a JSON-RPC URL), --decimals for a token (6 by default).
// Prints one JSON line, { hash, height, receiver, amount, token }, for the test or the recorder that called it.
import { Account, JsonRpcProvider, KeyPairSigner } from "near-api-js";

const args = {};
for (let i = 2; i < process.argv.length; i += 2)
  if (process.argv[i].startsWith("--"))
    args[process.argv[i].slice(2)] = process.argv[i + 1];
const payerId = process.env.NEAR_PAYER_ID;
const payerKey = process.env.NEAR_PAYER_KEY;
const network = process.env.NEAR_NETWORK ?? "testnet";
const nodeUrl = process.env.NEAR_RPC ?? (network === "mainnet" ? "https://free.rpc.fastnear.com" : "https://test.rpc.fastnear.com");
if (!payerId || !payerKey || !args.to || !args.amount) {
  console.error("usage: NEAR_PAYER_ID=... NEAR_PAYER_KEY=... node pay.mjs --to <account> --amount <decimal> [--token <contract> --memo <text> --decimals 6]");
  process.exit(2);
}

function toUnits(amount, decimals) {
  const [whole, fraction = ""] = String(amount).split(".");
  if (fraction.length > decimals) throw new Error(`more than ${decimals} decimals`);
  return BigInt(whole + fraction.padEnd(decimals, "0"));
}

async function rpc(method, params) {
  const response = await fetch(nodeUrl, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ jsonrpc: "2.0", id: "pay", method, params }),
  });
  const json = await response.json();
  if (json.error) throw new Error(JSON.stringify(json.error));
  return json.result;
}

const provider = new JsonRpcProvider({ url: nodeUrl });
const payer = new Account(payerId, provider, KeyPairSigner.fromSecretKey(payerKey));
const gas = 30_000_000_000_000n;
let outcome;
let amount;
if (!args.token) {
  amount = toUnits(args.amount, 24);
  outcome = await payer.transfer({ receiverId: args.to, amount });
} else {
  amount = toUnits(args.amount, Number(args.decimals ?? 6));
  const registration = await provider.callFunction({ contractId: args.token, method: "storage_balance_of", args: { account_id: args.to } });
  if (registration === null || registration === undefined) {
    await payer.callFunction({
      contractId: args.token,
      methodName: "storage_deposit",
      args: { account_id: args.to, registration_only: true },
      deposit: 1_250_000_000_000_000_000_000n,
      gas,
    });
  }
  outcome = await payer.callFunctionRaw({
    contractId: args.token,
    methodName: "ft_transfer",
    args: { receiver_id: args.to, amount: amount.toString(), memo: args.memo ?? null },
    deposit: 1n,
    gas,
  });
}
const block = await rpc("block", { block_id: outcome.transaction_outcome.block_hash });
console.log(JSON.stringify({
  hash: outcome.transaction.hash,
  height: block.header.height,
  receiver: args.to,
  amount: amount.toString(),
  token: args.token ?? "native",
  status: outcome.final_execution_status,
}));
