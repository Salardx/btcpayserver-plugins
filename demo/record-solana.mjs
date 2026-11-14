// Records the payment flow of the Solana plugin in a browser, against a BTCPay Server started by DemoHost (or any
// BTCPay Server with the plugin), on Solana devnet: a store is created over the Greenfield API with one receiving
// wallet, two invoices are open on that one address at the same time, the checkout's Solana Pay link is paid with
// pay-solana.mjs (the Solana Pay reference implementation), the checkout turns paid, and the invoice's payment list
// is shown. Then the same in USDC. One page, one video (webm) plus screenshots, under out/.
//
//   BTCPAY_URL, BTCPAY_EMAIL, BTCPAY_PASSWORD   or DEMO_HOST_FILE pointing at DemoHost's demo-host.json (default: ./demo-host.json)
//   SOLANA_SHOP                                 the wallet that receives
//   SOLANA_PAYER_KEY                            the payer's devnet keypair in Base58, for pay-solana.mjs
//   SOLANA_USDC                                 optional: the USDC mint the server watches (default: Circle's devnet mint)
//
// The USDC invoice is left out when the payer holds too little USDC (https://faucet.circle.com gives devnet USDC).
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const here = dirname(fileURLToPath(import.meta.url));
const hostFile = process.env.DEMO_HOST_FILE ?? join(process.cwd(), "demo-host.json");
const hostInfo = existsSync(hostFile) ? JSON.parse(readFileSync(hostFile, "utf8")) : {};
const base = (process.env.BTCPAY_URL ?? hostInfo.url ?? "").replace(/\/$/, "");
const email = process.env.BTCPAY_EMAIL ?? hostInfo.email;
const password = process.env.BTCPAY_PASSWORD ?? hostInfo.password;
const shop = process.env.SOLANA_SHOP;
const usdc = process.env.SOLANA_USDC || "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";
const solAmount = process.env.DEMO_SOL_AMOUNT ?? "0.05";
const usdcAmount = process.env.DEMO_USDC_AMOUNT ?? "1";
if (!base || !email || !password || !shop || !process.env.SOLANA_PAYER_KEY) {
  console.error("record-solana.mjs needs BTCPAY_URL/EMAIL/PASSWORD (or demo-host.json), SOLANA_SHOP and SOLANA_PAYER_KEY");
  process.exit(2);
}
const out = join(here, "out", new Date().toISOString().replace(/[:.]/g, "-"));
mkdirSync(out, { recursive: true });
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const authorization = "Basic " + Buffer.from(`${email}:${password}`).toString("base64");
async function api(method, path, body) {
  const response = await fetch(base + path, {
    method,
    headers: { Authorization: authorization, "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json();
}

/**
 * The payment method becomes available once the listener has reached the tip, up to two minutes after it is enabled;
 * until then BTCPay answers 400 ("payment method unavailable") or leaves the method out of the invoice. Keep asking.
 */
async function invoiceWhenReady(paymentMethodId, currency, amount) {
  for (let attempt = 1; attempt <= 40; attempt++) {
    let invoice;
    try {
      invoice = await api("POST", `/api/v1/stores/${store.id}/invoices`, {
        amount,
        currency,
        checkout: { paymentMethods: [paymentMethodId], lazyPaymentMethods: false, expirationMinutes: 30 },
      });
    } catch (error) {
      if (!/-> 400 /.test(String(error))) throw error;
      log(`${paymentMethodId} not available yet (attempt ${attempt}); waiting`);
      await sleep(6000);
      continue;
    }
    const methods = await api("GET", `/api/v1/stores/${store.id}/invoices/${invoice.id}/payment-methods`);
    const method = methods.find((m) => m.paymentMethodId === paymentMethodId);
    if (method) return { invoice, method };
    log(`${paymentMethodId} missing from the invoice (attempt ${attempt}); waiting`);
    await api("DELETE", `/api/v1/stores/${store.id}/invoices/${invoice.id}`).catch(() => {});
    await sleep(6000);
  }
  throw new Error(`${paymentMethodId} never became available`);
}

/** Runs pay-solana.mjs and returns the JSON line it prints. */
function payer(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(here, "pay-solana.mjs"), ...args], { env: process.env, stdio: ["ignore", "pipe", "inherit"] });
    let output = "";
    child.stdout.on("data", (d) => (output += d));
    child.on("close", (code) => (code === 0 ? resolve(JSON.parse(output.trim().split("\n").pop())) : reject(new Error(`pay-solana.mjs exited with ${code}`))));
  });
}

const balances = await payer(["--balances", "--token", usdc]);
log(`payer ${balances.payer} holds ${balances.sol} SOL and ${balances.token} USDC`);
const withUsdc = Number(balances.token) >= Number(usdcAmount);
if (!withUsdc) log("too little USDC for the token invoice; recording the SOL invoices only");

log("creating the store");
const store = await api("POST", "/api/v1/stores", { name: "Solana demo store" });
await api("PUT", `/api/v1/stores/${store.id}/payment-methods/SOL-SOLANA`, { enabled: true, config: { addresses: [shop] } });
if (withUsdc) {
  // A wallet that pays by Solana Pay does not create the receiver's token account, so the plugin asks for it to exist.
  const opened = await payer(["--open-token-account", shop, "--token", usdc]);
  log(opened.created ? `opened the shop's USDC account ${opened.tokenAccount}` : `the shop's USDC account ${opened.tokenAccount} exists`);
  await api("PUT", `/api/v1/stores/${store.id}/payment-methods/USDC-SOLANA`, { enabled: true, config: { addresses: [shop] } });
}

// Two invoices at once on the store's one address: each link carries its own reference, and only the second is paid.
log(`two invoices for ${solAmount} SOL on one address`);
const unpaid = await invoiceWhenReady("SOL-SOLANA", "SOL", solAmount);
const steps = [
  { currency: "SOL", ready: await invoiceWhenReady("SOL-SOLANA", "SOL", solAmount) },
  ...(withUsdc ? [{ currency: "USDC", pmi: "USDC-SOLANA", amount: usdcAmount }] : []),
];

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1280, height: 720 },
  recordVideo: { dir: out, size: { width: 1280, height: 720 } },
});
const page = await context.newPage();
let shots = 0;
const shot = async (name) => page.screenshot({ path: join(out, `${String(++shots).padStart(2, "0")}-${name}.png`) });
const summary = { base, store: store.id, sharedAddress: shop, unpaidInvoiceOnTheSameAddress: unpaid.invoice.id, payments: [] };

try {
  log("logging in");
  await page.goto(`${base}/login`);
  await page.fill("#Email", email);
  await page.fill("#Password", password);
  await page.click("#LoginButton");
  await page.waitForLoadState("networkidle");

  log("store settings");
  await page.goto(`${base}/stores/${store.id}/solana/SOL-SOLANA`);
  await page.getByText("payment settings").first().waitFor();
  await sleep(2500);
  await shot("store-solana-settings");

  for (const step of steps) {
    if (!step.ready) {
      log(`invoice for ${step.amount} ${step.currency}`);
      step.ready = await invoiceWhenReady(step.pmi, step.currency, step.amount);
    }
    const { invoice, method } = step.ready;
    if (!/^solana:[1-9A-HJ-NP-Za-km-z]{32,44}\?amount=[0-9.]+(&spl-token=[1-9A-HJ-NP-Za-km-z]{32,44})?&reference=[1-9A-HJ-NP-Za-km-z]{32,44}$/.test(method.paymentLink ?? ""))
      throw new Error(`the checkout link is not a Solana Pay transfer request with a reference: ${method.paymentLink}`);
    await page.goto(`${base}/i/${invoice.id}`);
    await page.waitForSelector("#Checkout .qr-container", { timeout: 60000 });
    await sleep(3000);
    await shot(`checkout-${step.currency}`);

    log("paying the checkout link on devnet:", method.paymentLink);
    const payment = await payer(["--url", method.paymentLink]);
    const sentAt = Date.now();
    log("sent", payment.hash, "in slot", payment.height, "| valid by the Solana Pay reference implementation:", payment.validatedBySolanaPay);

    // The checkout is open while the listener records the payment; it turns to "Invoice Paid" by itself.
    await page.waitForSelector("#settled", { timeout: 150000 });
    const seconds = Math.round((Date.now() - sentAt) / 100) / 10;
    log(`checkout paid ${seconds} s after the transaction was final`);
    summary.payments.push({
      invoice: invoice.id, link: method.paymentLink, ...payment,
      explorer: `https://explorer.solana.com/tx/${payment.hash}?cluster=devnet`, paidAfterSeconds: seconds,
    });
    await sleep(2500);
    await shot(`checkout-paid-${step.currency}`);

    await page.goto(`${base}/invoices/${invoice.id}`);
    const payments = page.getByText("Solana payments").first();
    await payments.waitFor({ timeout: 30000 });
    await sleep(2000);
    await payments.scrollIntoViewIfNeeded();
    await sleep(3000);
    await shot(`invoice-${step.currency}`);
  }

  // The other invoice on the same address was not touched by the payment of its neighbour.
  const other = await api("GET", `/api/v1/stores/${store.id}/invoices/${unpaid.invoice.id}`);
  summary.unpaidInvoiceStatus = other.status;
  if (other.status !== "New") throw new Error(`the invoice that was not paid is ${other.status}, not New`);
  summary.ok = true;
} catch (error) {
  summary.ok = false;
  summary.error = String(error);
  await shot("failure").catch(() => {});
  log("failed:", error);
} finally {
  const video = page.video();
  await context.close();
  await browser.close();
  if (video) {
    const path = await video.path();
    renameSync(path, join(out, "demo.webm"));
  }
  writeFileSync(join(out, "summary.json"), JSON.stringify(summary, null, 2));
  log("output in", out);
  process.exit(summary.ok ? 0 : 1);
}
