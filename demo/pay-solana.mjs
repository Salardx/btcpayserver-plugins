// Pays a BTCPay invoice on Solana devnet the way a Solana Pay wallet would, from a throwaway account. The transfer is
// built by the Solana Pay reference implementation (@solana/pay), from the very link the checkout shows:
//
//   node pay-solana.mjs --url "solana:<wallet>?amount=0.05&reference=<reference>"      a checkout link (SOL, or a token with spl-token=)
//   node pay-solana.mjs --to <wallet> --amount 0.05 [--token <mint>]                    a transfer typed by hand: it carries no reference
//   node pay-solana.mjs --open-token-account <wallet> --token <mint>                    creates the wallet's token account; the payer pays for it
//   node pay-solana.mjs --balances [--token <mint>]                                     what the payer holds
//
// The payer comes from the environment: SOLANA_PAYER_KEY (the 64-byte keypair in Base58, a devnet key). Never commit it.
// Optional: SOLANA_RPC (default https://api.devnet.solana.com). The node must be on devnet: any other cluster is refused.
// Prints one JSON line for the test or the recorder that called it.
import { createTransfer, parseURL, validateTransfer } from "@solana/pay";
import {
  address,
  appendTransactionMessageInstructions,
  createKeyPairSignerFromBytes,
  createSolanaRpc,
  createTransactionMessage,
  getBase58Encoder,
  getBase64EncodedWireTransaction,
  getSignatureFromTransaction,
  pipe,
  setTransactionMessageFeePayerSigner,
  setTransactionMessageLifetimeUsingBlockhash,
  signTransactionMessageWithSigners,
} from "@solana/kit";
import { fetchMint, findAssociatedTokenPda, getCreateAssociatedTokenIdempotentInstruction } from "@solana-program/token";

const DEVNET_GENESIS = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";

const args = {};
for (let i = 2; i < process.argv.length; i++) {
  if (!process.argv[i].startsWith("--")) continue;
  const value = process.argv[i + 1];
  args[process.argv[i].slice(2)] = value === undefined || value.startsWith("--") ? true : value;
}
const payerKey = process.env.SOLANA_PAYER_KEY ?? "";
const nodeUrl = process.env.SOLANA_RPC ?? "https://api.devnet.solana.com";
const mode = args.url ? "url" : args["open-token-account"] ? "open" : args.balances ? "balances" : args.to && args.amount ? "manual" : null;
if (!payerKey || !mode || (mode === "open" && typeof args.token !== "string")) {
  console.error('usage: SOLANA_PAYER_KEY=<base58 keypair> node pay-solana.mjs --url "solana:..." | --to <wallet> --amount <decimal> [--token <mint>] | --open-token-account <wallet> --token <mint> | --balances [--token <mint>]');
  process.exit(2);
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const rpc = createSolanaRpc(nodeUrl);
const payer = await createKeyPairSignerFromBytes(getBase58Encoder().encode(payerKey));

async function tokenAccount(owner, mint) {
  const { programAddress } = await fetchMint(rpc, mint);
  const [account] = await findAssociatedTokenPda({ owner, tokenProgram: programAddress, mint });
  return { account, tokenProgram: programAddress };
}

/** Signs, sends and waits until the cluster has finalized the transaction; no websocket, so it runs anywhere. */
async function send(instructions) {
  const { value: blockhash } = await rpc.getLatestBlockhash({ commitment: "finalized" }).send();
  const message = pipe(
    createTransactionMessage({ version: 0 }),
    (m) => setTransactionMessageFeePayerSigner(payer, m),
    (m) => setTransactionMessageLifetimeUsingBlockhash(blockhash, m),
    (m) => appendTransactionMessageInstructions(instructions, m),
  );
  const transaction = await signTransactionMessageWithSigners(message);
  const signature = getSignatureFromTransaction(transaction);
  await rpc.sendTransaction(getBase64EncodedWireTransaction(transaction), { encoding: "base64", preflightCommitment: "confirmed" }).send();
  for (let attempt = 0; attempt < 90; attempt++) {
    await sleep(1000);
    // Asked with the history, so a node that no longer has the signature in its short-lived cache still answers; a
    // throttled or failed question is simply asked again.
    const { value } = await rpc.getSignatureStatuses([signature], { searchTransactionHistory: true }).send().catch(() => ({ value: [null] }));
    const status = value[0];
    if (status?.err) throw new Error(`transaction ${signature} failed: ${JSON.stringify(status.err, (_, v) => (typeof v === "bigint" ? v.toString() : v))}`);
    if (status?.confirmationStatus === "finalized") return { signature, slot: Number(status.slot) };
  }
  throw new Error(`transaction ${signature} was not finalized within 90 seconds`);
}

// Exit codes are set, not forced: the process ends by itself once its connections are closed.
const genesis = await rpc.getGenesisHash().send();
if (genesis !== DEVNET_GENESIS) {
  console.error(`the node at ${new URL(nodeUrl).host} is not on devnet (genesis ${genesis}); this script pays on devnet only`);
  process.exitCode = 2;
} else if (mode === "balances") {
  const { value: lamports } = await rpc.getBalance(payer.address, { commitment: "finalized" }).send();
  const result = { payer: payer.address, sol: Number(lamports) / 1e9 };
  if (typeof args.token === "string") {
    const { account } = await tokenAccount(payer.address, address(args.token));
    const balance = await rpc.getTokenAccountBalance(account, { commitment: "finalized" }).send().catch(() => null);
    result.token = balance ? Number(balance.value.uiAmountString) : 0;
  }
  console.log(JSON.stringify(result));
} else if (mode === "open") {
  const owner = address(args["open-token-account"]);
  const mint = address(args.token);
  const { account, tokenProgram } = await tokenAccount(owner, mint);
  const existing = await rpc.getAccountInfo(account, { encoding: "base64", commitment: "finalized" }).send();
  if (existing.value) {
    console.log(JSON.stringify({ tokenAccount: account, created: false }));
  } else {
    const { signature, slot } = await send([getCreateAssociatedTokenIdempotentInstruction({ payer, ata: account, owner, mint, tokenProgram })]);
    console.log(JSON.stringify({ tokenAccount: account, created: true, hash: signature, height: slot }));
  }
} else {
  // A checkout link is read by the reference implementation's own parser: what it cannot read, no wallet can pay.
  const request = mode === "url"
    ? parseURL(args.url)
    : { recipient: address(args.to), amount: Number(args.amount), splToken: typeof args.token === "string" ? address(args.token) : undefined, reference: undefined };
  if (!("recipient" in request) || request.amount === undefined) {
    console.error("the link is not a Solana Pay transfer request with an amount");
    process.exitCode = 2;
  } else {
    const fields = { recipient: request.recipient, amount: request.amount, splToken: request.splToken, reference: request.reference, memo: request.memo };
    const { signature, slot } = await send(await createTransfer(rpc, payer, fields));
    // The reference implementation's own check of the finished payment, independent of the plugin's. The node that
    // answers can be a moment behind the one that reported the transaction final, so "not found" is asked again.
    let validated = true;
    for (let attempt = 1; ; attempt++) {
      try {
        await validateTransfer(rpc, signature, fields, { commitment: "finalized" });
        validated = true;
        break;
      } catch (error) {
        validated = String(error);
        if (!/not found/i.test(validated) || attempt === 15) break;
        await sleep(1000);
      }
    }
    console.log(JSON.stringify({
      hash: signature,
      height: slot,
      receiver: request.recipient,
      amount: String(request.amount),
      token: request.splToken ?? "native",
      reference: request.reference?.[0] ?? null,
      validatedBySolanaPay: validated,
      status: "finalized",
    }));
  }
}
