"use client";

import { useEffect } from "react";

// After the web app is rebuilt, an open tab still points at the previous build's code; moving to another page then
// fails to load that code. One reload picks up the new build; at most once a minute, so it can never loop.
const isStaleBuild = (error: Error) =>
  /ChunkLoadError|Loading chunk|dynamically imported module|Failed to load chunk|Failed to fetch/i.test(`${error.name} ${error.message}`);

/** Used by both error pages: the one inside the app and the last one above every layout. */
export function useReloadOnStaleBuild(error: Error) {
  useEffect(() => {
    if (!isStaleBuild(error)) return;
    try {
      const last = Number(sessionStorage.getItem("kasa-reloaded-at") ?? 0);
      if (Date.now() - last < 60_000) return;
      sessionStorage.setItem("kasa-reloaded-at", String(Date.now()));
    } catch {
      // Storage blocked: reload anyway, the browser keeps it from looping fast.
    }
    window.location.reload();
  }, [error]);
}
