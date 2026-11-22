"use client";

import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { useSession } from "./session";

interface Live {
  connected: boolean;
  /** Goes up by one each time the API says something changed for this company. Pages refetch when it moves. */
  version: number;
}

const LiveContext = createContext<Live>({ connected: false, version: 0 });

export function LiveProvider({ children }: { children: ReactNode }) {
  const { token, freshToken } = useSession();
  const [live, setLive] = useState<Live>({ connected: false, version: 0 });

  const signedIn = token !== null;
  useEffect(() => {
    if (!signedIn) return;
    const hub: HubConnection = new HubConnectionBuilder()
      // A token checked at every (re)connect: after sleep or a restart the one in memory has expired.
      .withUrl("/hubs/kasa", { accessTokenFactory: async () => (await freshToken()) ?? "" })
      // Never give up: 2, 4, 6… seconds, then every 30.
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (c) => Math.min(30_000, 2_000 * (c.previousRetryCount + 1)) })
      .configureLogging(LogLevel.Warning)
      .build();
    // A bulk export sends one "changed" per voucher; pages refetch at most once a second.
    let pending: ReturnType<typeof setTimeout> | undefined;
    hub.on("changed", () => {
      pending ??= setTimeout(() => {
        pending = undefined;
        setLive((l) => ({ ...l, version: l.version + 1 }));
      }, 1_000);
    });
    hub.onreconnecting(() => setLive((l) => ({ ...l, connected: false })));
    // After a gap, refetch once: changes during the gap were missed.
    hub.onreconnected(() => setLive((l) => ({ connected: true, version: l.version + 1 })));
    hub.onclose(() => setLive((l) => ({ ...l, connected: false })));
    hub.start().then(() => setLive((l) => ({ ...l, connected: true }))).catch(() => undefined);
    return () => {
      clearTimeout(pending);
      if (hub.state !== HubConnectionState.Disconnected) hub.stop();
    };
  }, [signedIn, freshToken]);

  return <LiveContext.Provider value={live}>{children}</LiveContext.Provider>;
}

export const useLive = () => useContext(LiveContext);
