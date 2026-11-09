// Records the NEAR payment flow in a browser, against a BTCPay Server started by DemoHost (or any BTCPay Server with the
// NEAR plugin): a store is created over the Greenfield API with the NEAR payment method on the given account, an
// invoice is opened in the checkout, the pay page is shown, a real testnet payment is sent with pay.mjs, and the page
// turns paid, then the invoice's payment list is shown. One page, one video (webm) plus screenshots, under out/.
//
//   BTCPAY_URL, BTCPAY_EMAIL, BTCPAY_PASSWORD   or DEMO_HOST_FILE pointing at DemoHost's demo-host.json (default: ./demo-host.json)
//   NEAR_SHOP                                   the testnet account that receives (must exist)
//   NEAR_PAYER_ID, NEAR_PAYER_KEY               the payer, for pay.mjs
//   NEAR_USDC                                   optional: the USDC contract on testnet; adds a USDC invoice paid with ft_transfer
//   DEMO_NEAR_AMOUNT (0.25), DEMO_USDC_AMOUNT (1)
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
const shop = process.env.NEAR_SHOP;
const usdc = process.env.NEAR_USDC;
const nearAmount = process.env.DEMO_NEAR_AMOUNT ?? "0.25";
const usdcAmount = process.env.DEMO_USDC_AMOUNT ?? "1";
if (!base || !email || !password || !shop || !process.env.NEAR_PAYER_ID || !process.env.NEAR_PAYER_KEY) {
  console.error("record.mjs needs BTCPAY_URL/EMAIL/PASSWORD (or demo-host.json), NEAR_SHOP, NEAR_PAYER_ID and NEAR_PAYER_KEY");
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

/** The payment method becomes available once the listener has reached the tip, up to a minute after it is enabled. */
async function invoiceWhenReady(paymentMethodId, currency, amount) {
  for (let attempt = 1; attempt <= 40; attempt++) {
    const invoice = await api("POST", `/api/v1/stores/${store.id}/invoices`, {
      amount,
      currency,
      checkout: { paymentMethods: [paymentMethodId], lazyPaymentMethods: false, expirationMinutes: 30 },
    });
    const methods = await api("GET", `/api/v1/stores/${store.id}/invoices/${invoice.id}/payment-methods`);
    const method = methods.find((m) => m.paymentMethodId === paymentMethodId);
    if (method) return { invoice, method };
    log(`${paymentMethodId} not available yet (attempt ${attempt}); waiting`);
    await api("DELETE", `/api/v1/stores/${store.id}/invoices/${invoice.id}`).catch(() => {});
    await sleep(6000);
  }
  throw new Error(`${paymentMethodId} never became available`);
}

function pay(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(here, "pay.mjs"), ...args], { env: process.env, stdio: ["ignore", "pipe", "inherit"] });
    let output = "";
    child.stdout.on("data", (d) => (output += d));
    child.on("close", (code) => (code === 0 ? resolve(JSON.parse(output.trim().split("\n").pop())) : reject(new Error(`pay.mjs exited with ${code}`))));
  });
}

log("creating the store");
const store = await api("POST", "/api/v1/stores", { name: "NEAR demo store" });
await api("PUT", `/api/v1/stores/${store.id}/payment-methods/NEAR-NEAR`, { enabled: true, config: { addresses: [shop] } });
if (usdc) await api("PUT", `/api/v1/stores/${store.id}/payment-methods/USDC-NEAR`, { enabled: true, config: { addresses: [shop] } });

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
  await page.goto(`${base}/stores/${store.id}/near/NEAR-NEAR`);
  await page.getByText("payment settings").first().waitFor();
  await sleep(2500);
  await shot("store-near-settings");

  for (const step of [
    { pmi: "NEAR-NEAR", currency: "NEAR", amount: nearAmount, payArgs: ["--amount", nearAmount] },
    ...(usdc ? [{ pmi: "USDC-NEAR", currency: "USDC", amount: usdcAmount, payArgs: ["--amount", usdcAmount, "--token", usdc] }] : []),
  ]) {
    log(`invoice for ${step.amount} ${step.currency}`);
    const { invoice, method } = await invoiceWhenReady(step.pmi, step.currency, step.amount);
    await page.goto(`${base}/i/${invoice.id}`);
    await page.waitForSelector("#PayInWallet", { timeout: 60000 });
    await sleep(2500);
    await shot(`checkout-${step.currency}`);

    const payUrl = await page.getAttribute("#PayInWallet", "href");
    log("pay page", payUrl);
    await page.goto(payUrl);
    await page.waitForSelector("#near-pay");
    await sleep(3500);
    await shot(`pay-page-${step.currency}`);

    log("sending the payment on testnet");
    const payment = await pay(["--to", method.destination, ...step.payArgs, "--memo", invoice.id]);
    log("sent", payment.hash, "in block", payment.height);
    summary.payments.push({ invoice: invoice.id, ...payment });

    await page.waitForSelector("#near-pay-status.alert-success", { timeout: 150000 });
    await sleep(2000);
    await shot(`pay-page-paid-${step.currency}`);

    await page.goto(`${base}/i/${invoice.id}`);
    await page.getByText("Invoice Paid").first().waitFor({ timeout: 60000 });
    await sleep(2500);
    await shot(`checkout-paid-${step.currency}`);

    await page.goto(`${base}/invoices/${invoice.id}`);
    await page.getByText("NEAR payments").first().waitFor({ timeout: 30000 });
    await sleep(4000);
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
