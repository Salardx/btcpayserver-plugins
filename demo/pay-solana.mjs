// Pays a BTCPay invoice on Solana devnet the way a Solana Pay wallet would, from a throwaway account. The transfer is
// built by the Solana Pay reference implementation (@solana/pay), from the very link the checkout shows:
//
//   node pay-solana.mjs --url "solana:<wallet>?amount=0.05&reference=<reference>"      a checkout link (SOL, or a token with spl-token=)
//   node pay-solana.mjs --to <wallet> --amount 0.05 [--token <mint>]                    a transfer typed by hand: it carries no reference
//   node pay-solana.mjs --open-token-account <wallet> --token <mint>                    creates the wallet's token account; the payer pays for it
//   node pay-solana.mjs --balances [--token <mint>]                                     what the payer holds
//   node pay-solana.mjs --transaction <base64> [--url "solana:..."]                     signs and sends a transfer the plugin's pay page built, as a browser wallet does
//
// With --url next to --transaction the prepared transfer is first compared with what the reference implementation
// builds from that checkout link (program, accounts, amount, reference); a transfer that differs is not signed.
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
  decompileTransactionMessage,
  getBase58Encoder,
  getBase64EncodedWireTransaction,
  getBase64Encoder,
  getCompiledTransactionMessageDecoder,
  getSignatureFromTransaction,
  getTransactionDecoder,
  isSignerRole,
  isWritableRole,
  pipe,
  setTransactionMessageFeePayerSigner,
  setTransactionMessageLifetimeUsingBlockhash,
  signTransaction,
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
const mode = typeof args.transaction === "string" ? "transaction"
  : args.url ? "url" : args["open-token-account"] ? "open" : args.balances ? "balances" : args.to && args.amount ? "manual" : null;
if (!payerKey || !mode || (mode === "open" && typeof args.token !== "string")) {
  console.error('usage: SOLANA_PAYER_KEY=<base58 keypair> node pay-solana.mjs --url "solana:..." | --to <wallet> --amount <decimal> [--token <mint>] | --open-token-account <wallet> --token <mint> | --balances [--token <mint>] | --transaction <base64> [--url "solana:..."]');
  process.exit(2);
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const rpc = createSolanaRpc(nodeUrl);
// The key is read behind a message of our own: what the decoder throws carries the value it was given, and this
// script's output is forwarded by the recorder.
let payer;
try {
  payer = await createKeyPairSignerFromBytes(getBase58Encoder().encode(payerKey));
} catch {
  console.error("SOLANA_PAYER_KEY is not a 64-byte Solana keypair in Base58");
  process.exit(2);
}

async function tokenAccount(owner, mint) {
  const { programAddress } = await fetchMint(rpc, mint);
  const [account] = await findAssociatedTokenPda({ owner, tokenProgram: programAddress, mint });
  return { account, tokenProgram: programAddress };
}

/** Builds a transaction from the instructions, signs it and sends it. */
async function send(instructions) {
  const { value: blockhash } = await rpc.getLatestBlockhash({ commitment: "finalized" }).send();
  const message = pipe(
    createTransactionMessage({ version: 0 }),
    (m) => setTransactionMessageFeePayerSigner(payer, m),
    (m) => setTransactionMessageLifetimeUsingBlockhash(blockhash, m),
    (m) => appendTransactionMessageInstructions(instructions, m),
  );
  return submit(await signTransactionMessageWithSigners(message));
}

/** Sends a signed transaction and waits until the cluster has finalized it; no websocket, so it runs anywhere. */
async function submit(transaction) {
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

/** The fields of a checkout link, read by the reference implementation's own parser: what it cannot read, no wallet can pay. */
function fieldsOf(link) {
  const request = parseURL(link);
  if (!("recipient" in request) || request.amount === undefined) return null;
  return { recipient: request.recipient, amount: request.amount, splToken: request.splToken, reference: request.reference, memo: request.memo };
}

/**
 * The reference implementation's own check of a finished payment, independent of the plugin's: true, or what it
 * objected to. The node that answers can be a moment behind the one that reported the transaction final, so
 * "not found" is asked again.
 */
async function validate(signature, fields) {
  for (let attempt = 1; ; attempt++) {
    try {
      await validateTransfer(rpc, signature, fields, { commitment: "finalized" });
      return true;
    } catch (error) {
      if (!/not found/i.test(String(error)) || attempt === 15) return String(error);
      await sleep(1000);
    }
  }
}

/**
 * How a prepared transaction differs from the transfer the reference implementation builds for the same request,
 * or null when it is that transfer: one instruction, the same program, the same data (so the same amount), the same
 * accounts in the same order (so the same receiver and the same reference) with the same roles. The payer also pays
 * the fee, which makes its account writable wherever it appears.
 */
function differenceFromReference(message, expected) {
  if (message.feePayer.address !== payer.address) return `the fee payer is ${message.feePayer.address}, not this payer`;
  if (message.instructions.length !== 1 || expected.length !== 1) return `it has ${message.instructions.length} instructions where the reference implementation builds ${expected.length}`;
  const [actual] = message.instructions;
  const [wanted] = expected;
  if (actual.programAddress !== wanted.programAddress) return `it calls ${actual.programAddress}, not ${wanted.programAddress}`;
  const data = (instruction) => Buffer.from(instruction.data ?? []).toString("hex");
  if (data(actual) !== data(wanted)) return `its instruction data is ${data(actual)}, not ${data(wanted)}`;
  const accounts = actual.accounts ?? [];
  const wantedAccounts = wanted.accounts ?? [];
  if (accounts.length !== wantedAccounts.length) return `it names ${accounts.length} accounts, not ${wantedAccounts.length}`;
  for (let i = 0; i < accounts.length; i++) {
    if (accounts[i].address !== wantedAccounts[i].address) return `account ${i} is ${accounts[i].address}, not ${wantedAccounts[i].address}`;
    if (isSignerRole(accounts[i].role) !== isSignerRole(wantedAccounts[i].role)) return `account ${i} signs in one and not in the other`;
    if (isWritableRole(accounts[i].role) !== isWritableRole(wantedAccounts[i].role) && accounts[i].address !== payer.address) return `account ${i} is writable in one and not in the other`;
  }
  return null;
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
} else if (mode === "transaction") {
  // What a browser wallet is handed by the plugin's pay page: an unsigned transaction with this payer as the only
  // signer. A wallet shows it and signs; this one first proves what it is about to sign.
  const transaction = getTransactionDecoder().decode(getBase64Encoder().encode(args.transaction));
  const message = decompileTransactionMessage(getCompiledTransactionMessageDecoder().decode(transaction.messageBytes));
  const signers = Object.keys(transaction.signatures);
  const fields = typeof args.url === "string" ? fieldsOf(args.url) : undefined;
  let refused = signers.length === 1 && signers[0] === payer.address ? null : `it wants the signatures of ${signers.join(", ")}, not of this payer alone`;
  if (!refused && fields === null) refused = "the link next to it is not a Solana Pay transfer request with an amount";
  // The transfer was built elsewhere, so the blockhash it names is what says which network it was built for: a
  // devnet node knows no mainnet blockhash, and knows its own only while they are still usable.
  if (!refused && !(await rpc.isBlockhashValid(message.lifetimeConstraint.blockhash, { commitment: "confirmed" }).send()).value)
    refused = "the blockhash it names is not one this devnet node will take: it was built for another network, or it has gone stale";
  if (!refused && fields) refused = differenceFromReference(message, await createTransfer(rpc, payer, fields));
  if (refused) {
    console.error(`the prepared transaction is not signed: ${refused}`);
    console.log(JSON.stringify({ refused }));
    process.exitCode = 3;
  } else {
    const { signature, slot } = await submit(await signTransaction([payer.keyPair], transaction));
    console.log(JSON.stringify({
      hash: signature,
      height: slot,
      receiver: fields?.recipient ?? null,
      amount: fields ? String(fields.amount) : null,
      token: fields ? fields.splToken ?? "native" : null,
      reference: fields?.reference?.[0] ?? null,
      sameAsReferenceImplementation: fields ? true : null,
      validatedBySolanaPay: fields ? await validate(signature, fields) : null,
      status: "finalized",
    }));
  }
} else {
  const fields = mode === "url"
    ? fieldsOf(args.url)
    : { recipient: address(args.to), amount: Number(args.amount), splToken: typeof args.token === "string" ? address(args.token) : undefined, reference: undefined };
  if (!fields) {
    console.error("the link is not a Solana Pay transfer request with an amount");
    process.exitCode = 2;
  } else {
    const { signature, slot } = await send(await createTransfer(rpc, payer, fields));
    console.log(JSON.stringify({
      hash: signature,
      height: slot,
      receiver: fields.recipient,
      amount: String(fields.amount),
      token: fields.splToken ?? "native",
      reference: fields.reference?.[0] ?? null,
      validatedBySolanaPay: await validate(signature, fields),
      status: "finalized",
    }));
  }
}
