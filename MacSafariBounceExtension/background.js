'use strict';

function injectIntoOpenTabs() {
  try {
    chrome.tabs.query({}, (tabs) => {
      if (chrome.runtime.lastError || !Array.isArray(tabs)) return;
      for (const tab of tabs) {
        if (!tab || typeof tab.id !== 'number' || !tab.url) continue;
        if (!/^(https?|file):/i.test(tab.url)) continue;
        try {
          chrome.scripting
            .executeScript({
              target: { tabId: tab.id },
              files: ['content.js']
            })
            .catch(() => {});
        } catch {
          // Ignore restricted/discarded tabs
        }
      }
    });
  } catch {
    // Non-fatal
  }
}

chrome.runtime.onInstalled.addListener(() => injectIntoOpenTabs());
chrome.runtime.onStartup.addListener(() => injectIntoOpenTabs());
injectIntoOpenTabs();
