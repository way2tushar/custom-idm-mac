const fields = ["enabled", "launchApp", "extensions", "minSizeKB"];

browser.runtime.sendMessage({ type: "status" }).then((response) => {
  const alive = response?.alive;
  document.getElementById("dot").className = `dot ${alive ? "on" : "off"}`;
  document.getElementById("state").textContent = alive ? "Connected to MacDM" : "MacDM is not running";
});

// The background script owns the defaults, merged with anything the user saved.
browser.runtime.sendMessage({ type: "settings" }).then((prefs) => {
  for (const name of fields) {
    const element = document.getElementById(name);
    if (!element || !(name in prefs)) continue; // Safari's popup has no size filter
    if (element.type === "checkbox") element.checked = prefs[name];
    else element.value = prefs[name];
    element.addEventListener("change", () => {
      const next = element.type === "checkbox" ? element.checked
        : element.type === "number" ? Number(element.value) : element.value;
      browser.storage.local.set({ [name]: next });
    });
  }
});

// Media the grabber found on the active tab.
browser.tabs.query({ active: true, currentWindow: true }).then(([tab]) => {
  if (!tab) return;
  browser.runtime.sendMessage({ type: "media-list", tabId: tab.id }).then((media) => {
    if (!media?.length) return;
    const list = document.getElementById("media");
    list.textContent = "";
    for (const entry of media) {
      const fileName = decodeURIComponent(new URL(entry.url).pathname.split("/").pop() || entry.url);
      if (entry.blocked) continue;
      const choices = entry.variants?.length
        ? entry.variants.map((v) => ({ label: v.label, variant: v.bandwidth, quality: v.label.split(" ")[0] }))
        : [{ label: fileName }];
      for (const choice of choices) {
        const row = document.createElement("li");
        const kind = document.createElement("span");
        kind.className = "kind";
        kind.textContent = entry.kind === "dash" ? "DASH" : entry.kind === "hls" ? "HLS" : "File";
        const name = document.createElement("span");
        name.className = "name";
        name.textContent = choice.label;
        name.title = entry.url;
        const button = document.createElement("button");
        button.textContent = "Download";
        button.addEventListener("click", () => {
          browser.runtime.sendMessage({ type: "download-media", url: entry.url, kind: entry.kind, title: tab.title, tabId: tab.id,
                                       variant: choice.variant, quality: choice.quality })
            .then((ok) => { button.textContent = ok ? "Sent" : "Failed"; button.disabled = true; });
        });
        row.append(kind, name, button);
        list.appendChild(row);
      }
    }
  });
});
