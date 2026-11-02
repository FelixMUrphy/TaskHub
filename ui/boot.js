"use strict";
const TOKEN = __TOKEN__;
const AUTOHIDE = __AUTOHIDE__;
const NATIVE = !!(window.chrome && window.chrome.webview);   // TaskHub .NET host (WebView2)
const host = m => { try{ window.chrome.webview.postMessage(m); }catch(e){} };
// a script error in the panel is otherwise seen by nobody: to the log (what and where, nothing from the page's data)
if (NATIVE){
  window.addEventListener("error",e=>host({ui_error:`${e.message} @ ${(e.filename||"").split("/").pop()}:${e.lineno}:${e.colno}`}));
  window.addEventListener("unhandledrejection",e=>host({ui_error:"rejection: "+((e.reason&&e.reason.message)||e.reason)}));
}
