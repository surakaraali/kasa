"use client";

import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useSession } from "./session";

interface Live {
  connected: boolean;
  /** Goes up by one each time the API says something changed for this company. Pages refetch when it moves. */
  version: number;
}

const LiveContext = createContext<Live>({ connected: false, version: 0 });

export function LiveProvider({ children }: { children: ReactNode }) {
  const { token } = useSession();
  const tokenRef = useRef(token);
  const [live, setLive] = useState<Live>({ connected: false, version: 0 });

  useEffect(() => {
    tokenRef.current = token;
  }, [token]);

  const signedIn = token !== null;
  useEffect(() => {
    if (!signedIn) return;
    const hub: HubConnection = new HubConnectionBuilder()
      // Read at every (re)connect, so a refreshed token is used.
      .withUrl("/hubs/kasa", { accessTokenFactory: () => tokenRef.current ?? "" })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    hub.on("changed", () => setLive((l) => ({ ...l, version: l.version + 1 })));
    hub.onreconnecting(() => setLive((l) => ({ ...l, connected: false })));
    // After a gap, refetch once: changes during the gap were missed.
    hub.onreconnected(() => setLive((l) => ({ connected: true, version: l.version + 1 })));
    hub.onclose(() => setLive((l) => ({ ...l, connected: false })));
    hub.start().then(() => setLive((l) => ({ ...l, connected: true }))).catch(() => undefined);
    return () => {
      if (hub.state !== HubConnectionState.Disconnected) hub.stop();
    };
  }, [signedIn]);

  return <LiveContext.Provider value={live}>{children}</LiveContext.Provider>;
}

export const useLive = () => useContext(LiveContext);
