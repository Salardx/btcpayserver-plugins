// Records the payment flow of an EVM plugin in a browser, against a BTCPay Server started by DemoHost (or any BTCPay
// Server with the plugin): a store is created over the Greenfield API with the payment method on the given address,
// an invoice is opened in the checkout, a real testnet payment is sent with pay-evm.mjs, the checkout turns paid, and
// the invoice's payment list is shown. One page, one video (webm) plus screenshots, under out/.
//
//   BTCPAY_URL, BTCPAY_EMAIL, BTCPAY_PASSWORD   or DEMO_HOST_FILE pointing at DemoHost's demo-host.json (default: ./demo-host.json)
//   EVM_PRESET                                  which plugin and testnet: bnb (default), arc, avalanche or flare
//   EVM_SHOP                                    the address that receives
//   EVM_PAYER_KEY                               the payer's testnet key, for pay-evm.mjs
//
// The second invoice of each preset is in a token; it is left out when the payer holds too little of it.
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

/**
 * A preset names the plugin's pages, the testnet, and two invoices: one in the chain's coin and one in a token.
 * `invoiceCurrency` prices the coin invoice in another currency, which the plugin then converts at its own rate
 * (on Flare: dollars, converted by the FTSO oracle). The token contracts are the ones the plugin watches on that
 * testnet by default, except on BNB Smart Chain, where DemoHost is started with BSC_USDT_CONTRACT set to this one.
 */
const PRESETS = {
  bnb: {
    plugin: "bnbchain", pluginName: "BNB Chain", chainId: "97", rpc: "https://bsc-testnet-rpc.publicnode.com",
    explorer: "https://testnet.bscscan.com/tx/{tx}",
    coin: { method: "BNB-BSC", currency: "BNB", amount: "0.001" },
    token: { method: "USDT-BSC", currency: "USDT", amount: "5", contract: "0x337610d27c682E347C9cD60BD4b3b107C9d34dDd", decimals: "18" },
  },
  arc: {
    plugin: "arc", pluginName: "Arc", chainId: "5042002", rpc: "https://rpc.testnet.arc.io",
    explorer: "https://explorer.testnet.arc.io/tx/{tx}",
    coin: { method: "USDC-ARC", currency: "USDC", amount: "1" },
    token: { method: "EURC-ARC", currency: "EURC", amount: "1", contract: "0x89B50855Aa3bE2F677cD6303Cec089B5F319D72a", decimals: "6" },
  },
  avalanche: {
    plugin: "avalanche", pluginName: "Avalanche", chainId: "43113", rpc: "https://api.avax-test.network/ext/bc/C/rpc",
    explorer: "https://testnet.snowtrace.io/tx/{tx}",
    coin: { method: "AVAX-AVAXC", currency: "AVAX", amount: "0.01" },
    token: { method: "USDC-AVAXC", currency: "USDC", amount: "1", contract: "0x5425890298aed601595a70AB815c96711a31Bc65", decimals: "6" },
  },
  flare: {
    plugin: "flare", pluginName: "Flare", chainId: "114", rpc: "https://coston2-api.flare.network/ext/C/rpc",
    explorer: "https://coston2-explorer.flare.network/tx/{tx}",
    coin: { method: "FLR-FLARE", currency: "FLR", amount: "0.10", invoiceCurrency: "USD" },
    token: { method: "FXRP-FLARE", currency: "FXRP", amount: "1", contract: "0x0b6A3645c240605887a5532109323A3E12273dc7", decimals: "6" },
  },
};

const here = dirname(fileURLToPath(import.meta.url));
const hostFile = process.env.DEMO_HOST_FILE ?? join(process.cwd(), "demo-host.json");
const hostInfo = existsSync(hostFile) ? JSON.parse(readFileSync(hostFile, "utf8")) : {};
const base = (process.env.BTCPAY_URL ?? hostInfo.url ?? "").replace(/\/$/, "");
const email = process.env.BTCPAY_EMAIL ?? hostInfo.email;
const password = process.env.BTCPAY_PASSWORD ?? hostInfo.password;
const shop = process.env.EVM_SHOP;
const preset = PRESETS[process.env.EVM_PRESET || "bnb"];
if (!preset) {
  console.error(`EVM_PRESET must be one of: ${Object.keys(PRESETS).join(", ")}`);
  process.exit(2);
}
if (!base || !email || !password || !shop || !process.env.EVM_PAYER_KEY) {
  console.error("record-evm.mjs needs BTCPAY_URL/EMAIL/PASSWORD (or demo-host.json), EVM_SHOP and EVM_PAYER_KEY");
  process.exit(2);
}
const { plugin, pluginName, explorer } = preset;
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

/** Runs pay-evm.mjs on the preset's testnet and returns the JSON line it prints. */
function payer(args) {
  return new Promise((resolve, reject) => {
    const env = { ...process.env, EVM_CHAIN_ID: preset.chainId, EVM_RPC: process.env.EVM_RPC || preset.rpc };
    const child = spawn(process.execPath, [join(here, "pay-evm.mjs"), ...args], { env, stdio: ["ignore", "pipe", "inherit"] });
    let output = "";
    child.stdout.on("data", (d) => (output += d));
    child.on("close", (code) => (code === 0 ? resolve(JSON.parse(output.trim().split("\n").pop())) : reject(new Error(`pay-evm.mjs exited with ${code}`))));
  });
}

const balances = await payer(["--show", "balances", "--token", preset.token.contract, "--decimals", preset.token.decimals]);
log(`payer ${balances.payer} holds ${balances.coin} ${preset.coin.currency} and ${balances.token} ${preset.token.currency}`);
const withToken = Number(balances.token) >= Number(preset.token.amount);
if (!withToken) log(`too little ${preset.token.currency} for the token invoice; recording the ${preset.coin.currency} invoice only`);

log("creating the store");
const store = await api("POST", "/api/v1/stores", { name: `${pluginName} demo store` });
await api("PUT", `/api/v1/stores/${store.id}/payment-methods/${preset.coin.method}`, { enabled: true, config: { addresses: [shop] } });
if (withToken) await api("PUT", `/api/v1/stores/${store.id}/payment-methods/${preset.token.method}`, { enabled: true, config: { addresses: [shop] } });

const steps = [
  { ...preset.coin, payArgs: [] },
  ...(withToken ? [{ ...preset.token, payArgs: ["--token", preset.token.contract, "--decimals", preset.token.decimals] }] : []),
];
const priced = (step) => `${step.amount} ${step.invoiceCurrency ?? step.currency}`;
// The first invoice waits for the listener before the browser opens, so the recording has no dead time.
log(`invoice for ${priced(steps[0])}`);
steps[0].ready = await invoiceWhenReady(steps[0].method, steps[0].invoiceCurrency ?? steps[0].currency, steps[0].amount);

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1280, height: 720 },
  recordVideo: { dir: out, size: { width: 1280, height: 720 } },
});
const page = await context.newPage();
let shots = 0;
const shot = async (name) => page.screenshot({ path: join(out, `${String(++shots).padStart(2, "0")}-${name}.png`) });
const summary = { base, preset: process.env.EVM_PRESET || "bnb", store: store.id, payments: [] };

try {
  log("logging in");
  await page.goto(`${base}/login`);
  await page.fill("#Email", email);
  await page.fill("#Password", password);
  await page.click("#LoginButton");
  await page.waitForLoadState("networkidle");

  log("store settings");
  await page.goto(`${base}/stores/${store.id}/${plugin}/${preset.coin.method}`);
  await page.getByText("payment settings").first().waitFor();
  await sleep(2500);
  await shot(`store-${plugin}-settings`);

  for (const step of steps) {
    if (!step.ready) {
      log(`invoice for ${priced(step)}`);
      step.ready = await invoiceWhenReady(step.method, step.invoiceCurrency ?? step.currency, step.amount);
    }
    const { invoice, method } = step.ready;
    await page.goto(`${base}/i/${invoice.id}`);
    await page.waitForSelector("#Checkout .qr-container", { timeout: 60000 });
    await sleep(3000);
    await shot(`checkout-${step.currency}`);

    // What the checkout asks for: the invoice's amount, or its conversion at the plugin's rate.
    log(`sending ${method.due} ${step.currency} on testnet to`, method.destination);
    const payment = await payer(["--to", method.destination, "--amount", String(method.due), ...step.payArgs]);
    const sentAt = Date.now();
    log("sent", payment.hash, "in block", payment.height);

    // The checkout is open while the listener records the payment; it turns to "Invoice Paid" by itself.
    await page.waitForSelector("#settled", { timeout: 150000 });
    const seconds = Math.round((Date.now() - sentAt) / 100) / 10;
    log(`checkout paid ${seconds} s after the transaction was mined`);
    summary.payments.push({
      invoice: invoice.id, invoiced: priced(step), paid: `${method.due} ${step.currency}`, rate: method.rate,
      ...payment, explorer: explorer.replace("{tx}", payment.hash), paidAfterSeconds: seconds,
    });
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
