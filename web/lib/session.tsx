"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { Me } from "./types";

/** An API error with the message the API wrote for people (problem details: detail, title or validation errors). */
export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

async function errorOf(response: Response): Promise<ApiError> {
  let message = `İstek başarısız oldu (${response.status}).`;
  try {
    const body = await response.json();
    const validation = body.errors ? Object.values(body.errors as Record<string, string[]>).flat().join(" ") : null;
    message = validation || body.detail || body.title || message;
  } catch {
    // Not JSON.
  }
  return new ApiError(response.status, message);
}

// One refresh at a time. Refresh tokens work once and a second use signs every session of the user out (theft
// detection). Within a tab the promise is shared; across tabs, which share the cookie, a browser lock makes the
// second tab wait and then send the cookie the first one rotated.
let refreshing: Promise<string | null> | null = null;

function refreshOnce(): Promise<string | null> {
  const call = () => fetch("/api/auth/refresh", { method: "POST" })
    .then(async (r) => (r.ok ? ((await r.json()).accessToken as string) : null))
    .catch(() => null);
  // The DOM types give request() a Promise of a Promise; at run time it resolves to the token, which await unwraps.
  const locked = async () => (typeof navigator !== "undefined" && navigator.locks ? await navigator.locks.request("kasa-refresh", call) : await call());
  refreshing ??= locked()
    .finally(() => {
      refreshing = null;
    });
  return refreshing;
}

/** The access token's own expiry (a JWT carries it): within 30 seconds counts as expired. */
function expiresSoon(jwt: string): boolean {
  try {
    const { exp } = JSON.parse(atob(jwt.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
    return exp * 1000 < Date.now() + 30_000;
  } catch {
    return true;
  }
}

interface Session {
  ready: boolean;
  me: Me | null;
  token: string | null;
  /** Calls the API with the access token; on 401 refreshes once and tries again. */
  api: <T>(path: string, init?: RequestInit & { json?: unknown }) => Promise<T>;
  /** The same, but hands back the raw response (for streams). Throws ApiError when it is not ok. */
  authFetch: (path: string, init?: RequestInit & { json?: unknown }) => Promise<Response>;
  signIn: (path: "/api/auth/login" | "/api/auth/register" | "/api/auth/accept-invitation", body: unknown) => Promise<void>;
  signOut: () => Promise<void>;
  /** Re-reads the signed-in user, with a new access token when the API handed one out (after a password change). */
  reload: (accessToken?: string) => Promise<void>;
  /** An access token good for at least 30 more seconds, refreshed first when needed; null once signed out. */
  freshToken: () => Promise<string | null>;
}

const SessionContext = createContext<Session | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  // The access token lives in memory only: never in localStorage, where any script on the page could read it.
  const token = useRef<string | null>(null);
  const [state, setState] = useState<{ ready: boolean; me: Me | null; token: string | null }>({ ready: false, me: null, token: null });

  const loadMe = useCallback(async (accessToken: string | null) => {
    token.current = accessToken;
    if (!accessToken) {
      setState({ ready: true, me: null, token: null });
      return;
    }
    const r = await fetch("/api/me", { headers: { Authorization: `Bearer ${accessToken}` } });
    setState({ ready: true, me: r.ok ? await r.json() : null, token: r.ok ? accessToken : null });
  }, []);

  useEffect(() => {
    refreshOnce().then(loadMe);
  }, [loadMe]);

  const authFetch = useCallback(async (path: string, init: RequestInit & { json?: unknown } = {}): Promise<Response> => {
    const send = (accessToken: string | null) => {
      const headers = new Headers(init.headers);
      if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);
      if (init.json !== undefined) headers.set("Content-Type", "application/json");
      return fetch(path, { ...init, headers, body: init.json !== undefined ? JSON.stringify(init.json) : init.body });
    };

    let response = await send(token.current);
    if (response.status === 401) {
      const fresh = await refreshOnce();
      if (!fresh) {
        await loadMe(null);
        throw new ApiError(401, "Oturumun süresi doldu. Tekrar giriş yapın.");
      }
      token.current = fresh;
      setState((s) => ({ ...s, token: fresh }));
      response = await send(fresh);
    }
    if (!response.ok) throw await errorOf(response);
    return response;
  }, [loadMe]);

  const api = useCallback(async <T,>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> => {
    const response = await authFetch(path, init);
    return (response.status === 204 ? undefined : await response.json()) as T;
  }, [authFetch]);

  const signIn = useCallback<Session["signIn"]>(async (path, body) => {
    const r = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    if (!r.ok) throw await errorOf(r);
    await loadMe((await r.json()).accessToken);
  }, [loadMe]);

  const signOut = useCallback(async () => {
    await fetch("/api/auth/logout", { method: "POST" });
    await loadMe(null);
  }, [loadMe]);

  const reload = useCallback((accessToken?: string) => loadMe(accessToken ?? token.current), [loadMe]);

  const freshToken = useCallback(async () => {
    if (token.current && !expiresSoon(token.current)) return token.current;
    const fresh = await refreshOnce();
    if (fresh) {
      token.current = fresh;
      setState((s) => ({ ...s, token: fresh }));
    }
    return fresh;
  }, []);

  const value = useMemo(() => ({ ...state, api, authFetch, signIn, signOut, reload, freshToken }), [state, api, authFetch, signIn, signOut, reload, freshToken]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession() {
  const session = useContext(SessionContext);
  if (!session) throw new Error("useSession outside SessionProvider");
  return session;
}

export const canEdit = (me: Me | null) => me?.role === "Admin" || me?.role === "Accountant";
