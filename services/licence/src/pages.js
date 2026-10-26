// The pages a buyer sees after Stripe Checkout. Plain HTML, light and dark, no scripts except Copy.

const STYLE = `
:root { color-scheme: light dark; --bg: #fafafa; --fg: #1c1c1e; --muted: #6e6e73; --card: #fff; --line: #d9d9de; --accent: #3584e4; }
@media (prefers-color-scheme: dark) { :root { --bg: #1e1e20; --fg: #f2f2f7; --muted: #a1a1a6; --card: #2c2c2e; --line: #3a3a3c; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.5 system-ui, sans-serif; }
main { max-width: 560px; margin: 12vh auto; padding: 0 16px; }
h1 { font-size: 26px; margin: 0 0 8px; }
p { color: var(--muted); margin: 0 0 16px; }
.key { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 14px 16px; font: 14px/1.4 ui-monospace, monospace; word-break: break-all; user-select: all; }
button { margin-top: 12px; border: 0; border-radius: 999px; padding: 10px 22px; font: inherit; font-weight: 600; background: var(--accent); color: #fff; cursor: pointer; }
ol { color: var(--muted); padding-left: 20px; }
`;

function page(title, body) {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escape(title)} · HDMI Captain</title><style>${STYLE}</style></head><body><main>${body}</main></body></html>`;
}

export function keyPage(key) {
  return page('Your licence key', `<h1>Thank you for buying HDMI Captain Pro</h1>
<p>This is your licence key. Keep it somewhere safe; it works on up to three computers.</p>
<div class="key" id="key">${escape(key)}</div>
<button type="button" onclick="navigator.clipboard.writeText(document.getElementById('key').textContent).then(()=>{this.textContent='Copied'})">Copy Key</button>
<h2 style="font-size:18px;margin:28px 0 8px">To unlock Pro</h2>
<ol><li>Open HDMI Captain.</li><li>Open the menu and choose <b>HDMI Captain Pro…</b></li><li>Paste the key and choose <b>Activate</b>.</li></ol>`);
}

export function messagePage(title, text) {
  return page(title, `<h1>${escape(title)}</h1><p>${escape(text)}</p>`);
}

export function cancelledPage() {
  return messagePage('No payment was made', 'You can close this page. HDMI Captain keeps working as a free viewer.');
}

function escape(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}
