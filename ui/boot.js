"use strict";
const TOKEN = __TOKEN__;
const AUTOHIDE = __AUTOHIDE__;
const NATIVE = !!(window.chrome && window.chrome.webview);   // TaskHub .NET host (WebView2)
const host = m => { try{ window.chrome.webview.postMessage(m); }catch(e){} };
