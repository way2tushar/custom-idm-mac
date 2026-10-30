// Intercepts clicks on links that point at downloadable files and sends them to MacDM.
// If MacDM can't take the link, the click is replayed so Safari downloads it normally.

let prefs = null;
const passThrough = new WeakSet();

browser.runtime.sendMessage({ type: "settings" }).then((value) => { prefs = value; });

function wantedExtension(url) {
  if (!prefs?.enabled) return false;
  const match = /\.([a-z0-9]{1,8})$/i.exec(url.pathname);
  return !!match && prefs.extensions.split(/[\s,]+/).includes(match[1].toLowerCase());
}

document.addEventListener("click", async (event) => {
  if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  const anchor = event.target instanceof Element ? event.target.closest("a[href]") : null;
  if (!anchor || passThrough.has(anchor)) return;

  let url;
  try { url = new URL(anchor.href, location.href); } catch { return; }
  if (!/^https?:$/.test(url.protocol)) return;
  if (!anchor.hasAttribute("download") && !wantedExtension(url)) return;

  event.preventDefault();
  event.stopImmediatePropagation();
  const accepted = await browser.runtime.sendMessage({
    type: "download",
    url: url.href,
    referrer: location.href,
    // Only cookies visible to the page are available here; HttpOnly ones are not.
    cookies: url.origin === location.origin ? document.cookie : "",
    filename: anchor.getAttribute("download") || null,
    auto: true, // MacDM may decline it (excluded site or address); then the click goes to Safari
  }).catch(() => false);

  if (!accepted) {
    passThrough.add(anchor);
    anchor.click();
    setTimeout(() => passThrough.delete(anchor), 1000);
  }
}, true);
