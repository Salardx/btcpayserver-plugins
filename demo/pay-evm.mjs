// Pays a BTCPay invoice on an EVM testnet the way a customer's wallet would, from a throwaway account:
//
//   node pay-evm.mjs --to 0x<address> --amount 0.01                      the chain's coin, a plain transfer
//   node pay-evm.mjs --to 0x<address> --amount 5 --token 0x<contract>     an ERC-20 / BEP-20 transfer (--decimals 18 by default)
//   node pay-evm.mjs --show balances [--token 0x<contract> --decimals 6]   what the payer holds, without paying
//
// The payer comes from the environment: EVM_PAYER_KEY (0x and 64 hex digits, a testnet key). Never commit it.
// Optional: EVM_CHAIN_ID (97, the BNB Smart Chain testnet; 5042002 Arc testnet, 43113 Avalanche Fuji, 114 Flare Coston2)
// and EVM_RPC (a JSON-RPC URL of that chain; the testnets named here have a default).
// The node must report the same chain id, and the well-known mainnets are refused: this script is for demos.
// Prints one JSON line, { hash, height, receiver, amount, token, status }, for the test or the recorder that called it.
import { secp256k1 } from "@noble/curves/secp256k1.js";
import { keccak_256 } from "@noble/hashes/sha3.js";

const DEFAULT_RPC = {
  97: "https://bsc-testnet-rpc.publicnode.com",
  114: "https://coston2-api.flare.network/ext/C/rpc",
  43113: "https://api.avax-test.network/ext/bc/C/rpc",
  5042002: "https://rpc.testnet.arc.io",
};
const MAINNETS = [1n, 10n, 14n, 19n, 56n, 137n, 204n, 5042n, 8453n, 42161n, 43114n, 47763n];

const args = {};
for (let i = 2; i < process.argv.length; i += 2)
  if (process.argv[i].startsWith("--"))
    args[process.argv[i].slice(2)] = process.argv[i + 1];
const payerKey = process.env.EVM_PAYER_KEY ?? "";
const chainId = BigInt(process.env.EVM_CHAIN_ID ?? "97");
const nodeUrl = process.env.EVM_RPC ?? DEFAULT_RPC[chainId];
const isAddress = (value) => /^0x[0-9a-fA-F]{40}$/.test(value ?? "");
if (MAINNETS.includes(chainId)) {
  console.error(`chain ${chainId} is a mainnet; this script pays on testnets only`);
  process.exit(2);
}
const showBalances = args.show === "balances";
if (!/^0x[0-9a-fA-F]{64}$/.test(payerKey) || (!showBalances && (!isAddress(args.to) || !args.amount)) || (args.token && !isAddress(args.token)) || !nodeUrl) {
  console.error("usage: EVM_PAYER_KEY=0x... [EVM_CHAIN_ID=97 EVM_RPC=https://...] node pay-evm.mjs --to <address> --amount <decimal> [--token <contract> --decimals 18] | --show balances [--token <contract> --decimals 18]");
  process.exit(2);
}

const hex = (bytes) => Buffer.from(bytes).toString("hex");
const fromHex = (value) => Buffer.from(value.replace(/^0x/, ""), "hex");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function toUnits(amount, decimals) {
  const [whole, fraction = ""] = String(amount).split(".");
  if (fraction.length > decimals) throw new Error(`more than ${decimals} decimals`);
  return BigInt(whole + fraction.padEnd(decimals, "0"));
}

function fromUnits(units, decimals) {
  const digits = units.toString().padStart(decimals + 1, "0");
  const fraction = digits.slice(digits.length - decimals).replace(/0+$/, "");
  return digits.slice(0, digits.length - decimals) + (fraction ? "." + fraction : "");
}

async function rpc(method, params) {
  const response = await fetch(nodeUrl, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method, params }),
  });
  const json = await response.json();
  if (json.error) throw new Error(`${method}: ${JSON.stringify(json.error)}`);
  return json.result;
}

// RLP, as far as a transaction needs it: byte strings and lists of them; numbers are big-endian without leading zeros.
function quantity(value) {
  if (value === 0n) return Buffer.alloc(0);
  const digits = value.toString(16);
  return Buffer.from(digits.length % 2 ? "0" + digits : digits, "hex");
}
function lengthPrefix(length, offset) {
  if (length < 56) return Buffer.from([offset + length]);
  const size = quantity(BigInt(length));
  return Buffer.concat([Buffer.from([offset + 55 + size.length]), size]);
}
function rlp(item) {
  if (Array.isArray(item)) {
    const body = Buffer.concat(item.map(rlp));
    return Buffer.concat([lengthPrefix(body.length, 0xc0), body]);
  }
  if (item.length === 1 && item[0] < 0x80) return Buffer.from(item);
  return Buffer.concat([lengthPrefix(item.length, 0x80), Buffer.from(item)]);
}
const word = (bytes) => Buffer.concat([Buffer.alloc(32 - bytes.length), bytes]);

const secretKey = fromHex(payerKey);
const payer = "0x" + hex(keccak_256(secp256k1.getPublicKey(secretKey, false).slice(1)).slice(-20));

const reported = BigInt(await rpc("eth_chainId", []));
if (reported !== chainId) throw new Error(`the node is on chain ${reported}, not ${chainId}`);

/** Sends the payment and waits for its receipt; true when the transaction succeeded. */
async function payInvoice() {
  let to, value, data, amount, gasLimit;
  if (!args.token) {
    amount = toUnits(args.amount, 18);
    to = fromHex(args.to);
    value = amount;
    data = Buffer.alloc(0);
    gasLimit = 21000n;
  } else {
    amount = toUnits(args.amount, Number(args.decimals ?? 18));
    to = fromHex(args.token);
    value = 0n;
    data = Buffer.concat([fromHex("a9059cbb"), word(fromHex(args.to)), word(quantity(amount))]); // transfer(address,uint256)
    const held = BigInt(await rpc("eth_call", [{ to: args.token, data: "0x70a08231" + hex(word(fromHex(payer))) }, "latest"])); // balanceOf(address)
    if (held < amount) throw new Error(`the payer ${payer} holds ${held} units of ${args.token}, the payment needs ${amount}`);
    const estimate = BigInt(await rpc("eth_estimateGas", [{ from: payer, to: args.token, data: "0x" + hex(data) }]));
    gasLimit = (estimate * 3n) / 2n;
  }

  const gasPrice = (BigInt(await rpc("eth_gasPrice", [])) * 5n) / 4n;
  const balance = BigInt(await rpc("eth_getBalance", [payer, "latest"]));
  const needed = value + gasLimit * gasPrice;
  if (balance < needed) throw new Error(`the payer ${payer} holds ${balance} wei, the payment needs ${needed}`);
  const nonce = BigInt(await rpc("eth_getTransactionCount", [payer, "pending"]));

  // A legacy transaction with replay protection (EIP-155): sign over the chain id, then carry it in v.
  const fields = [quantity(nonce), quantity(gasPrice), quantity(gasLimit), to, quantity(value), data];
  const digest = keccak_256(rlp([...fields, quantity(chainId), Buffer.alloc(0), Buffer.alloc(0)]));
  const signature = secp256k1.sign(digest, secretKey, { prehash: false, format: "recovered" }); // recovery id, r, s
  if (hex(secp256k1.recoverPublicKey(signature, digest, { prehash: false })) !== hex(secp256k1.getPublicKey(secretKey, true)))
    throw new Error("the signature does not recover to the payer");
  const v = chainId * 2n + 35n + BigInt(signature[0]);
  const r = BigInt("0x" + hex(signature.slice(1, 33)));
  const s = BigInt("0x" + hex(signature.slice(33, 65)));
  const raw = rlp([...fields, quantity(v), quantity(r), quantity(s)]);

  const hash = await rpc("eth_sendRawTransaction", ["0x" + hex(raw)]);
  let receipt = null;
  for (let attempt = 0; attempt < 90 && !receipt; attempt++) {
    await sleep(1000);
    receipt = await rpc("eth_getTransactionReceipt", [hash]).catch(() => null);
  }
  if (!receipt) throw new Error(`no receipt for ${hash} after 90 seconds`);
  const ok = BigInt(receipt.status) === 1n;
  console.log(JSON.stringify({
    hash,
    height: Number(BigInt(receipt.blockNumber)),
    receiver: args.to,
    amount: amount.toString(),
    token: args.token ?? "native",
    status: ok ? "success" : "reverted",
  }));
  return ok;
}

// Exit codes are set, not forced: the process ends by itself once its connections are closed.
if (showBalances) {
  const result = { payer, coin: fromUnits(BigInt(await rpc("eth_getBalance", [payer, "latest"])), 18) };
  if (args.token)
    result.token = fromUnits(BigInt(await rpc("eth_call", [{ to: args.token, data: "0x70a08231" + hex(word(fromHex(payer))) }, "latest"])), Number(args.decimals ?? 18));
  console.log(JSON.stringify(result));
} else {
  process.exitCode = (await payInvoice()) ? 0 : 1;
}
