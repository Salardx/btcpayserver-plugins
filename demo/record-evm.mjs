// Records the payment flow of an EVM plugin in a browser, against a BTCPay Server started by DemoHost (or any BTCPay
// Server with the plugin): a store is created over the Greenfield API with the payment method on the given address,
// an invoice is opened in the checkout, a real testnet payment is sent with pay-evm.mjs, the checkout turns paid, and
// the invoice's payment list is shown. One page, one video (webm) plus screenshots, under out/.
// The defaults are the BNB Chain plugin on the BNB Smart Chain testnet.
//
//   BTCPAY_URL, BTCPAY_EMAIL, BTCPAY_PASSWORD   or DEMO_HOST_FILE pointing at DemoHost's demo-host.json (default: ./demo-host.json)
//   EVM_SHOP                                    the address that receives
//   EVM_PAYER_KEY                               the payer's testnet key, for pay-evm.mjs (with EVM_CHAIN_ID and EVM_RPC when not BNB testnet)
//   EVM_TOKEN                                   optional: a token contract the server watches (DemoHost: BSC_USDT_CONTRACT); adds a token invoice
//   EVM_PLUGIN (bnbchain), EVM_PLUGIN_NAME (BNB Chain), EVM_COIN (BNB), EVM_COIN_METHOD (BNB-BSC),
//   EVM_TOKEN_CURRENCY (USDT), EVM_TOKEN_METHOD (USDT-BSC), EVM_TOKEN_DECIMALS (18),
//   EVM_EXPLORER_TX (https://testnet.bscscan.com/tx/{tx}), DEMO_COIN_AMOUNT (0.01), DEMO_TOKEN_AMOUNT (5)
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
const shop = process.env.EVM_SHOP;
const token = process.env.EVM_TOKEN;
const plugin = process.env.EVM_PLUGIN ?? "bnbchain";
const pluginName = process.env.EVM_PLUGIN_NAME ?? "BNB Chain";
const coin = process.env.EVM_COIN ?? "BNB";
const coinMethod = process.env.EVM_COIN_METHOD ?? "BNB-BSC";
const tokenCurrency = process.env.EVM_TOKEN_CURRENCY ?? "USDT";
const tokenMethod = process.env.EVM_TOKEN_METHOD ?? "USDT-BSC";
const tokenDecimals = process.env.EVM_TOKEN_DECIMALS ?? "18";
const explorer = process.env.EVM_EXPLORER_TX ?? "https://testnet.bscscan.com/tx/{tx}";
const coinAmount = process.env.DEMO_COIN_AMOUNT ?? "0.01";
const tokenAmount = process.env.DEMO_TOKEN_AMOUNT ?? "5";
if (!base || !email || !password || !shop || !process.env.EVM_PAYER_KEY) {
  console.error("record-evm.mjs needs BTCPAY_URL/EMAIL/PASSWORD (or demo-host.json), EVM_SHOP and EVM_PAYER_KEY");
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

function pay(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(here, "pay-evm.mjs"), ...args], { env: process.env, stdio: ["ignore", "pipe", "inherit"] });
    let output = "";
    child.stdout.on("data", (d) => (output += d));
    child.on("close", (code) => (code === 0 ? resolve(JSON.parse(output.trim().split("\n").pop())) : reject(new Error(`pay-evm.mjs exited with ${code}`))));
  });
}

log("creating the store");
const store = await api("POST", "/api/v1/stores", { name: `${pluginName} demo store` });
await api("PUT", `/api/v1/stores/${store.id}/payment-methods/${coinMethod}`, { enabled: true, config: { addresses: [shop] } });
if (token) await api("PUT", `/api/v1/stores/${store.id}/payment-methods/${tokenMethod}`, { enabled: true, config: { addresses: [shop] } });

const steps = [
  { pmi: coinMethod, currency: coin, amount: coinAmount, payArgs: ["--amount", coinAmount] },
  ...(token ? [{ pmi: tokenMethod, currency: tokenCurrency, amount: tokenAmount, payArgs: ["--amount", tokenAmount, "--token", token, "--decimals", tokenDecimals] }] : []),
];
// The first invoice waits for the listener before the browser opens, so the recording has no dead time.
log(`invoice for ${steps[0].amount} ${steps[0].currency}`);
steps[0].ready = await invoiceWhenReady(steps[0].pmi, steps[0].currency, steps[0].amount);

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1280, height: 720 },
  recordVideo: { dir: out, size: { width: 1280, height: 720 } },
});
const page = await context.newPage();
let shots = 0;
const shot = async (name) => page.screenshot({ path: join(out, `${String(++shots).padStart(2, "0")}-${name}.png`) });
const summary = { base, store: store.id, payments: [] };

try {
  log("logging in");
  await page.goto(`${base}/login`);
  await page.fill("#Email", email);
  await page.fill("#Password", password);
  await page.click("#LoginButton");
  await page.waitForLoadState("networkidle");

  log("store settings");
  await page.goto(`${base}/stores/${store.id}/${plugin}/${coinMethod}`);
  await page.getByText("payment settings").first().waitFor();
  await sleep(2500);
  await shot(`store-${plugin}-settings`);

  for (const step of steps) {
    if (!step.ready) {
      log(`invoice for ${step.amount} ${step.currency}`);
      step.ready = await invoiceWhenReady(step.pmi, step.currency, step.amount);
    }
    const { invoice, method } = step.ready;
    await page.goto(`${base}/i/${invoice.id}`);
    await page.waitForSelector("#Checkout .qr-container", { timeout: 60000 });
    await sleep(3000);
    await shot(`checkout-${step.currency}`);

    log("sending the payment on testnet to", method.destination);
    const payment = await pay(["--to", method.destination, ...step.payArgs]);
    const sentAt = Date.now();
    log("sent", payment.hash, "in block", payment.height);

    // The checkout is open while the listener records the payment; it turns to "Invoice Paid" by itself.
    await page.waitForSelector("#settled", { timeout: 150000 });
    const seconds = Math.round((Date.now() - sentAt) / 100) / 10;
    log(`checkout paid ${seconds} s after the transaction was mined`);
    summary.payments.push({ invoice: invoice.id, ...payment, explorer: explorer.replace("{tx}", payment.hash), paidAfterSeconds: seconds });
    await sleep(2500);
    await shot(`checkout-paid-${step.currency}`);

    await page.goto(`${base}/invoices/${invoice.id}`);
    const payments = page.getByText(`${pluginName} payments`).first();
    await payments.waitFor({ timeout: 30000 });
    await sleep(2000);
    await payments.scrollIntoViewIfNeeded();
    await sleep(3000);
    await shot(`invoice-${step.currency}`);
  }
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
