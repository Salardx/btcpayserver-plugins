// The browser side of the NEAR pay page (Views/UINEARPay/Pay.cshtml). The page gives us the invoice in data
// attributes on #near-pay and a status area through window.nearPayShow; we list the NEAR wallets, connect one, and
// send exactly one transaction: a NEAR transfer, or ft_transfer on the token contract with the invoice id as the
// memo, preceded by storage_deposit when the store's account is not registered with the token yet. The page's own
// script polls the invoice status; nothing here talks to the store.
import { setupWalletSelector, actionCreators } from "@near-wallet-selector/core";
import { setupMyNearWallet } from "@near-wallet-selector/my-near-wallet";
import { setupMeteorWallet } from "@near-wallet-selector/meteor-wallet";
import { setupHotWallet } from "@near-wallet-selector/hot-wallet";
import { setupNightly } from "@near-wallet-selector/nightly";

const GAS_PER_CALL = 30000000000000n; // 30 TGas, plenty for storage_deposit and ft_transfer
const ONE_YOCTO = 1n; // ft_transfer demands exactly one yoctoNEAR attached
const STORAGE_DEPOSIT_FALLBACK = 1250000000000000000000n; // 0.00125 NEAR, the usual NEP-145 minimum
const SENT_TEXT = "Transaction sent. The store sees it within a few seconds…";
const NOT_LOADED_TEXT = "The wallet connection did not load in this browser. Pay with the details below instead.";

// The selector's own presets for everything but the RPC, which the page hands us.
const NETWORK_PRESETS = {
  mainnet: {
    helperUrl: "https://helper.mainnet.near.org",
    explorerUrl: "https://nearblocks.io",
    indexerUrl: "https://api.fastnear.com/v0",
  },
  testnet: {
    helperUrl: "https://helper.testnet.near.org",
    explorerUrl: "https://testnet.nearblocks.io",
    indexerUrl: "https://test.api.fastnear.com/v0",
  },
};

function show(kind, text) {
  if (typeof window.nearPayShow === "function") {
    window.nearPayShow(kind, text);
  }
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function errorText(error) {
  if (error && typeof error === "object") {
    const payload = error.payload;
    if (payload && typeof payload === "object" && (payload.message || payload.error)) {
      return String(payload.message || payload.error);
    }
    if (error.message) return String(error.message);
  }
  if (typeof error === "string" && error) return error;
  return "Unknown error";
}

// Decodes what a wallet put in the query string; MyNearWallet encodes its error message a second time.
function decodeParam(value) {
  if (!value || !value.includes("%")) return value;
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

// Sets the page up and returns nothing; every failure ends in the status area.
async function main() {
  const root = document.getElementById("near-pay");
  const mount = document.getElementById("near-wallet-mount");
  if (!root || !mount) return;
  const data = root.dataset;
  const page = {
    invoiceId: data.invoice || "",
    destination: data.destination || "",
    units: data.units || "",
    amount: data.amount || "",
    currency: data.currency || "",
    token: data.token || "",
    network: data.network === "mainnet" ? "mainnet" : "testnet",
    rpc: data.rpc || "",
    expiresAt: Number(data.expires) || 0,
  };

  function ready() {
    window.nearPayWalletsReady = true;
  }

  if (page.expiresAt && page.expiresAt * 1000 <= Date.now()) {
    show("warning", "This invoice has expired");
    mount.replaceChildren();
    ready();
    return;
  }
  reportWalletRedirect();

  if (!page.destination || !page.units) {
    mount.replaceChildren(element("p", "text-muted mb-0", NOT_LOADED_TEXT));
    ready();
    return;
  }

  // For a token, learn now whether the destination is registered with the contract (and what registration costs),
  // so the pay click itself does not wait on the RPC: wallets open a popup, and browsers only allow that shortly
  // after a click.
  const registration = page.token ? checkRegistration(page) : Promise.resolve(null);

  let selector;
  try {
    selector = await setupWalletSelector({
      network: page.rpc ? { networkId: page.network, nodeUrl: page.rpc, ...NETWORK_PRESETS[page.network] } : page.network,
      fallbackRpcUrls: page.rpc ? [page.rpc] : undefined,
      modules: [setupMyNearWallet(), setupMeteorWallet(), setupHotWallet(), setupNightly()],
    });
  } catch (error) {
    console.error("NEAR pay page: the wallet selector did not start", error);
    mount.replaceChildren(element("p", "text-muted mb-0", NOT_LOADED_TEXT));
    ready();
    return;
  }

  let busy = false; // a wallet is open, or a sign-out runs
  let sent = false; // the transaction went out; the page's polling takes over

  function moduleOf(id) {
    return selector.store.getState().modules.find((module) => module.id === id) || null;
  }

  function render() {
    const state = selector.store.getState();
    const account = state.accounts.find((candidate) => candidate.active) || state.accounts[0] || null;
    if (selector.isSignedIn() && account) {
      renderConnected(account, moduleOf(state.selectedWalletId));
    } else {
      renderWalletList(state.modules);
    }
    ready();
  }

  function renderWalletList(modules) {
    const wallets = modules.filter((module) => !module.metadata.deprecated && module.metadata.available);
    if (!wallets.length) {
      mount.replaceChildren(element("p", "text-muted mb-0", "No NEAR wallet is available in this browser. Pay with the details below instead."));
      return;
    }
    const list = element("div", "d-grid gap-2");
    for (const module of wallets) {
      const button = element("button", "btn btn-outline-secondary d-flex align-items-center gap-2 text-start");
      button.type = "button";
      button.disabled = busy;
      const icon = element("img", "rounded");
      icon.src = module.metadata.iconUrl;
      icon.alt = "";
      icon.width = 24;
      icon.height = 24;
      button.append(icon, element("span", "flex-grow-1", module.metadata.name));
      button.addEventListener("click", () => connect(module));
      list.append(button);
    }
    mount.replaceChildren(list);
  }

  function renderConnected(account, module) {
    const who = element("p", "mb-2");
    who.append("Connected as ", element("code", "", account.accountId));
    if (module) who.append(` via ${module.metadata.name}`);
    if (!busy && !sent) {
      const disconnect = element("a", "small ms-2", "disconnect");
      disconnect.href = "#";
      disconnect.addEventListener("click", (event) => {
        event.preventDefault();
        disconnectWallet();
      });
      who.append(disconnect);
    }
    const pay = element("button", "btn btn-primary");
    pay.type = "button";
    pay.disabled = busy || sent;
    pay.textContent = sent ? "Transaction sent" : busy ? "Waiting for the wallet…" : `Pay ${page.amount} ${page.currency}`;
    pay.addEventListener("click", () => payInvoice());
    mount.replaceChildren(who, pay);
  }

  async function connect(module) {
    if (busy) return;
    busy = true;
    render();
    try {
      const wallet = await selector.wallet(module.id);
      // Every module brings its own flow (popup, extension, HOT's widget); all of them need a contract id for the
      // selector's state, and the one the transaction goes to is the natural choice.
      await wallet.signIn({ contractId: page.token || page.destination, methodNames: [] });
    } catch (error) {
      let text = `${module.metadata.name} could not connect: ${errorText(error)}.`;
      if (module.metadata.downloadUrl && /not (injected|installed|available|found)/i.test(text)) {
        text += ` Get the wallet at ${module.metadata.downloadUrl}`;
      }
      show("danger", text);
    } finally {
      busy = false;
      render();
    }
  }

  async function disconnectWallet() {
    if (busy) return;
    busy = true;
    render();
    try {
      const wallet = await selector.wallet();
      await wallet.signOut();
    } catch (error) {
      // The wallet that was connected last time is gone (an extension removed, say): drop the selector's memory of
      // it and start over, which is what signing out would have done.
      console.warn("NEAR pay page: sign-out through the wallet failed, clearing the selection", error);
      for (const key of ["selectedWalletId", "contract", "recentlySignedInWallets"]) {
        window.localStorage.removeItem(`near-wallet-selector:${key}`);
      }
      window.location.reload();
      return;
    } finally {
      busy = false;
      render();
    }
  }

  async function payInvoice() {
    if (busy || sent) return;
    busy = true;
    render();
    try {
      const wallet = await selector.wallet();
      const transaction = await buildTransaction(page, await registration);
      show("info", "Approve the transaction in your wallet…");
      await wallet.signAndSendTransaction(transaction);
      sent = true;
      show("success", SENT_TEXT);
    } catch (error) {
      show("danger", `The transaction was not sent: ${errorText(error)}`);
    } finally {
      busy = false;
      render();
    }
  }

  // HOT and Nightly report sign-ins and account changes of their own; keep the view in step with the selector.
  selector.store.observable.subscribe(() => {
    if (!busy) render();
  });
  render();
}

// A wallet that left the page and came back (MyNearWallet when its popup is blocked) reports in the query string.
function reportWalletRedirect() {
  const query = new URLSearchParams(window.location.search);
  const hashes = query.get("transactionHashes");
  const errorCode = query.get("errorCode");
  const errorMessage = query.get("errorMessage");
  if (!hashes && !errorCode && !errorMessage) return;
  if (hashes) {
    show("success", SENT_TEXT);
  } else {
    show("warning", `The wallet reported: ${decodeParam(errorMessage) || errorCode}`);
  }
  // Drop the wallet's parameters so a reload does not repeat the message.
  for (const key of ["transactionHashes", "errorCode", "errorMessage", "signMeta"]) query.delete(key);
  const search = query.toString();
  window.history.replaceState(null, "", window.location.pathname + (search ? `?${search}` : "") + window.location.hash);
}

// The exact one transaction for the invoice, built from what the page says and what the token contract says.
async function buildTransaction(page, registration) {
  const { transfer, functionCall } = actionCreators;
  if (!page.token) {
    return { receiverId: page.destination, actions: [transfer(BigInt(page.units))] };
  }
  const actions = [];
  if (!registration || !registration.registered) {
    actions.push(
      functionCall(
        "storage_deposit",
        { account_id: page.destination, registration_only: true },
        GAS_PER_CALL,
        registration ? registration.minDeposit : STORAGE_DEPOSIT_FALLBACK,
      ),
    );
  }
  actions.push(
    functionCall(
      "ft_transfer",
      { receiver_id: page.destination, amount: page.units, memo: page.invoiceId },
      GAS_PER_CALL,
      ONE_YOCTO,
    ),
  );
  return { receiverId: page.token, actions };
}

// Whether the destination has a storage balance with the token, and the minimum a registration costs. When the RPC
// cannot tell, the transaction registers anyway: with registration_only the contract refunds a second registration.
async function checkRegistration(page) {
  let registered = false;
  let minDeposit = STORAGE_DEPOSIT_FALLBACK;
  try {
    registered = (await viewFunction(page, "storage_balance_of", { account_id: page.destination })) !== null;
  } catch (error) {
    console.warn("NEAR pay page: storage_balance_of failed, registering to be safe", error);
  }
  if (!registered) {
    try {
      const bounds = await viewFunction(page, "storage_balance_bounds", {});
      if (bounds && typeof bounds.min === "string" && /^\d+$/.test(bounds.min)) minDeposit = BigInt(bounds.min);
    } catch (error) {
      console.warn("NEAR pay page: storage_balance_bounds failed, using the usual minimum", error);
    }
  }
  return { registered, minDeposit };
}

// Calls a view method on the token contract through the page's RPC and returns its JSON result.
async function viewFunction(page, methodName, args) {
  const response = await fetch(page.rpc, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      jsonrpc: "2.0",
      id: "nearpay",
      method: "query",
      params: {
        request_type: "call_function",
        finality: "final",
        account_id: page.token,
        method_name: methodName,
        args_base64: btoa(JSON.stringify(args)),
      },
    }),
  });
  if (!response.ok) throw new Error(`RPC answered ${response.status}`);
  const body = await response.json();
  if (body.error) throw new Error(body.error.data || body.error.message || "RPC error");
  const bytes = body.result && body.result.result;
  if (!Array.isArray(bytes)) throw new Error("RPC answered without a result");
  const text = new TextDecoder().decode(Uint8Array.from(bytes));
  return text.length ? JSON.parse(text) : null;
}

main().catch((error) => {
  console.error("NEAR pay page failed", error);
  show("danger", `The wallet connection failed: ${errorText(error)}`);
  window.nearPayWalletsReady = true;
});
