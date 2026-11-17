// The pay page of the Solana plugin, in the browser. It finds the Solana wallets of this browser through the Wallet
// Standard (https://github.com/wallet-standard/wallet-standard), asks the server for this invoice's transfer, built
// for the connected account with the invoice's Solana Pay reference in it, and hands that one transaction to the
// wallet to sign and send. The page holds no key and talks to no Solana node: the wallet does both, and what became
// of a transaction it sent is asked of the server, which watches the chain anyway.
//
// Hand-written and served as it is: no bundle, no dependency.
(function () {
    'use strict';

    var root = document.getElementById('solana-pay');
    if (!root) {
        return;
    }
    var settings = root.dataset;
    var statusBox = document.getElementById('solana-pay-status');
    var walletList = document.getElementById('solana-pay-wallets');

    // The names the Wallet Standard and its Solana features define.
    var REGISTER_WALLET = 'wallet-standard:register-wallet';
    var APP_READY = 'wallet-standard:app-ready';
    var CONNECT = 'standard:connect';
    var SIGN_AND_SEND = 'solana:signAndSendTransaction';

    // How often the page asks about the invoice, and about a transaction a wallet said it sent.
    // The invoice's own state is BTCPay's to answer and costs it nothing. Asking what became of a sent transaction
    // costs the store's node a call and shares the server's public-page ration, so it is asked twice a minute.
    var INVOICE_INTERVAL = 3000;
    var SENT_INTERVAL = 30000;

    var chain = settings.chain;         // "solana:mainnet" or "solana:devnet"
    var wallets = [];
    var asking = false;                 // a wallet has been asked and has not answered yet
    var sent = null;                    // { signature, lastValidBlockHeight } of a payment that is on its way
    var closed = false;                 // the invoice is paid, expired or invalid

    function show(kind, text, signature) {
        statusBox.className = 'alert alert-' + kind;
        statusBox.textContent = text;
        if (signature && settings.explorer) {
            var link = document.createElement('a');
            link.href = settings.explorer.replace('{0}', encodeURIComponent(signature));
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.className = 'alert-link text-break';
            link.textContent = signature;
            statusBox.appendChild(document.createElement('br'));
            statusBox.appendChild(link);
        }
    }

    // ---- the invoice's state, from BTCPay's own status endpoint ----

    function poll() {
        fetch(settings.statusUrl, { credentials: 'same-origin' })
            .then(function (response) { return response.json(); })
            .then(function (model) {
                var state = String(model.status || '').toLowerCase();
                if (state === 'settled' || state === 'processing' || state === 'paid') {
                    closed = true;
                    show('success', 'Payment received. Thank you.', sent && sent.signature);
                    render();
                    return;
                }
                if (state === 'expired' || state === 'invalid') {
                    closed = true;
                    show('danger', sent
                        ? 'This invoice has ' + state + ' although a payment was sent from this page. Contact the store with this transaction:'
                        : 'This invoice has ' + state + '. Go back to the checkout for a new one.', sent && sent.signature);
                    render();
                    return;
                }
                setTimeout(poll, INVOICE_INTERVAL);
            })
            .catch(function () { setTimeout(poll, INVOICE_INTERVAL + 2000); });
    }

    // ---- what became of a transaction a wallet said it sent ----

    /**
     * A wallet can hand back a signature for a transaction that never reaches the network, and the blockhash the
     * transfer names goes stale after about half a minute. So the server is asked what the chain knows, until the
     * invoice closes or the payment turns out to be one that will never arrive.
     */
    function watchSent() {
        if (!sent || closed) {
            return;
        }
        var asked = sent;
        var query = '?signature=' + encodeURIComponent(asked.signature) + '&lastValidBlockHeight=' + encodeURIComponent(asked.lastValidBlockHeight || 0);
        fetch(settings.sentUrl + query, { credentials: 'same-origin' })
            .then(function (response) { return response.json(); })
            .then(function (answer) {
                if (sent !== asked || closed) {
                    return;
                }
                if (answer.state === 'gone') {
                    forget('The payment never reached the network, so nothing was paid.');
                } else if (answer.state === 'failed') {
                    forget('The payment failed on the network, so nothing was paid.');
                } else {
                    setTimeout(watchSent, SENT_INTERVAL);
                }
            })
            .catch(function () { setTimeout(watchSent, SENT_INTERVAL + 5000); });
    }

    /** The payment that was on its way is not coming: the page offers to pay again. */
    function forget(text) {
        sent = null;
        root.removeAttribute('data-sent');
        show('warning', text + ' You can pay again.');
        render();
    }

    function markSent(signature, lastValidBlockHeight, kind, text) {
        sent = { signature: signature, lastValidBlockHeight: lastValidBlockHeight || 0 };
        asking = false;
        root.setAttribute('data-sent', signature);
        show(kind, text, signature);
        render();
        setTimeout(watchSent, SENT_INTERVAL);
    }

    // ---- wallets ----

    function feature(wallet, name) {
        return wallet && wallet.features ? wallet.features[name] : undefined;
    }

    function includes(list, value) {
        return !!list && Array.prototype.indexOf.call(list, value) >= 0;
    }

    /** The Wallet Standard serves other chains too; a wallet without a Solana chain is none of this page's business. */
    function isSolanaWallet(wallet) {
        for (var i = 0; wallet.chains && i < wallet.chains.length; i++) {
            if (String(wallet.chains[i]).indexOf('solana:') === 0) {
                return true;
            }
        }
        return false;
    }

    /** Why a wallet cannot pay this invoice, or null when it can. */
    function unusable(wallet) {
        var send = feature(wallet, SIGN_AND_SEND);
        if (!feature(wallet, CONNECT) || !send || typeof send.signAndSendTransaction !== 'function') {
            return 'it cannot send a prepared payment';
        }
        if (!includes(wallet.chains, chain)) {
            return 'it does not offer Solana ' + settings.network;
        }
        if (send.supportedTransactionVersions && !includes(send.supportedTransactionVersions, 'legacy')) {
            return 'it does not take this transaction format';
        }
        return null;
    }

    function register() {
        for (var i = 0; i < arguments.length; i++) {
            var wallet = arguments[i];
            if (wallet && typeof wallet.name === 'string' && isSolanaWallet(wallet) && wallets.indexOf(wallet) < 0) {
                wallets.push(wallet);
            }
        }
        render();
        var registered = Array.prototype.slice.call(arguments);
        return function unregister() {
            wallets = wallets.filter(function (wallet) { return registered.indexOf(wallet) < 0; });
            render();
        };
    }

    function render() {
        walletList.textContent = '';
        var usable = 0;
        wallets.forEach(function (wallet) {
            var reason = unusable(wallet);
            if (reason) {
                var note = document.createElement('p');
                note.className = 'text-muted small mb-2';
                note.textContent = wallet.name + ' was found, but ' + reason + '.';
                walletList.appendChild(note);
                return;
            }
            usable++;
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn btn-primary me-2 mb-2';
            button.disabled = asking || closed || !!sent;
            if (typeof wallet.icon === 'string' && wallet.icon.indexOf('data:image/') === 0) {
                var icon = document.createElement('img');
                icon.src = wallet.icon;
                icon.alt = '';
                icon.width = 20;
                icon.height = 20;
                icon.className = 'me-2 align-text-bottom';
                button.appendChild(icon);
            }
            button.appendChild(document.createTextNode('Pay with ' + wallet.name));
            button.addEventListener('click', function () { pay(wallet); });
            walletList.appendChild(button);
        });
        if (usable === 0 && wallets.length === 0) {
            var none = document.createElement('p');
            none.className = 'text-muted mb-0';
            none.textContent = 'No Solana wallet was found in this browser. Open this page in a browser that has a Solana wallet extension, or in the browser of a wallet app, or pay with the wallet link below.';
            walletList.appendChild(none);
        }
        root.setAttribute('data-wallets', String(usable));
    }

    function reasonOf(error) {
        var text = error && error.message ? String(error.message) : String(error || '');
        return text.length > 300 ? text.slice(0, 300) + '…' : text;
    }

    /** A reason as a sentence of its own, whether or not it came with its full stop. */
    function sentence(text) {
        return /[.!?…]$/.test(text) ? text : text + '.';
    }

    function bytesOf(base64) {
        var binary = atob(base64);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes;
    }

    var ALPHABET = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';

    /** A transaction signature as explorers and nodes spell it. */
    function base58(bytes) {
        var digits = [0];
        var i, j, carry;
        for (i = 0; i < bytes.length; i++) {
            carry = bytes[i];
            for (j = 0; j < digits.length; j++) {
                carry += digits[j] << 8;
                digits[j] = carry % 58;
                carry = (carry / 58) | 0;
            }
            while (carry > 0) {
                digits.push(carry % 58);
                carry = (carry / 58) | 0;
            }
        }
        var text = '';
        for (i = 0; i < bytes.length && bytes[i] === 0; i++) {
            text += '1';
        }
        for (i = digits.length - 1; i >= 0; i--) {
            text += ALPHABET.charAt(digits[i]);
        }
        return text;
    }

    function accountFor(wallet, granted) {
        var accounts = granted && granted.accounts && granted.accounts.length ? granted.accounts : wallet.accounts;
        for (var i = 0; accounts && i < accounts.length; i++) {
            var account = accounts[i];
            if ((!account.chains || includes(account.chains, chain)) && (!account.features || includes(account.features, SIGN_AND_SEND))) {
                return account;
            }
        }
        return null;
    }

    /** The server builds the transfer for this account: the amount still due, the store's wallet, the invoice's reference. */
    function prepare(address) {
        return fetch(settings.transactionUrl, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ account: address })
        }).then(function (response) {
            return response.json().catch(function () { return {}; }).then(function (answer) {
                if (!response.ok || typeof answer.transaction !== 'string') {
                    var refusal = new Error(answer.message || 'The payment could not be prepared (' + response.status + '). Try again in a moment.');
                    // The server found a payment for this invoice on the chain that is not recorded yet: nothing more to send.
                    refusal.alreadySent = typeof answer.signature === 'string' ? answer.signature : null;
                    throw refusal;
                }
                return answer;
            });
        });
    }

    function pay(wallet) {
        if (asking || closed || sent) {
            return;
        }
        asking = true;
        render();
        show('secondary', 'Connecting ' + wallet.name + '…');
        var prepared = null;
        Promise.resolve()
            .then(function () { return feature(wallet, CONNECT).connect(); })
            .then(function (granted) {
                var account = accountFor(wallet, granted);
                if (!account) {
                    throw new Error(wallet.name + ' shared no account on Solana ' + settings.network + '.');
                }
                show('secondary', 'Preparing the payment…');
                return prepare(account.address).then(function (answer) {
                    prepared = answer;
                    // What the page was showing may be a moment old; the server names the amount it built for.
                    show('secondary', 'Approve the payment of ' + answer.amount + ' ' + answer.currency + ' in ' + wallet.name + '.');
                    return feature(wallet, SIGN_AND_SEND).signAndSendTransaction({
                        account: account,
                        transaction: bytesOf(answer.transaction),
                        chain: chain
                    });
                });
            })
            .then(function (outputs) {
                var output = outputs && outputs[0];
                if (!output || !output.signature || !output.signature.length) {
                    throw new Error(wallet.name + ' returned no transaction signature.');
                }
                markSent(base58(output.signature), prepared && prepared.lastValidBlockHeight, 'info',
                    'Payment sent. This page shows it as paid once the network has finalized it, usually within seconds. Do not pay again.');
            })
            .catch(function (error) {
                if (error && error.alreadySent) {
                    // Known to the chain already, so the server's own watch on it decides what happens next.
                    markSent(error.alreadySent, 0, 'info', reasonOf(error));
                    return;
                }
                asking = false;
                render();
                if (!closed) {
                    show('warning', 'No payment was confirmed. ' + sentence(reasonOf(error)) + ' You can try again.');
                }
            });
    }

    // ---- start ----

    // A wallet that loaded before this page announces itself when the page says it is ready; one that loads later
    // announces itself with an event of its own. Either way it is handed the same register function.
    var api = Object.freeze({ register: register });
    window.addEventListener(REGISTER_WALLET, function (event) {
        if (event && typeof event.detail === 'function') {
            try {
                event.detail(api);
            } catch (error) {
                // A wallet that fails to register is simply not listed.
            }
        }
    });
    try {
        window.dispatchEvent(new CustomEvent(APP_READY, { detail: api }));
    } catch (error) {
        // No wallet is told; the wallet link below still works.
    }
    render();
    poll();
})();
