"use client";

import { useCallback, useEffect, useState } from "react";
import { useLive } from "./live";
import { useSession } from "./session";

/** GETs a path, and again whenever the API pushes a change. A null path waits. */
export function useData<T>(path: string | null) {
  const { api } = useSession();
  const { version } = useLive();
  const [data, setData] = useState<T | undefined>();
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    if (!path) return;
    try {
      setData(await api<T>(path));
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, [api, path]);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- fetching on mount and on live changes is the point
    reload();
  }, [reload, version]);

  return { data, error, reload };
}
