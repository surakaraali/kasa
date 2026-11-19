"use client";

import { useEffect, useState } from "react";
import { useSession } from "./session";

// The photo needs the access token, so it cannot be a plain <img src>: it is fetched once per user and version,
// kept as a blob address, and shared by every place that shows the same person.
const photos = new Map<string, Promise<string>>();
const ready = new Map<string, string>();

/** A person's photo, or the first letter of their name until there is one. */
export function Avatar({ id, version, name, size = 28 }: { id: string; version: number | null; name: string; size?: number }) {
  const { authFetch } = useSession();
  const key = version == null ? null : `/api/users/${id}/avatar?v=${version}`;
  const [, loaded] = useState(0);

  useEffect(() => {
    if (!key || ready.has(key)) return;
    let live = true;
    if (!photos.has(key)) {
      photos.set(key, authFetch(key).then((r) => r.blob()).then((b) => {
        const url = URL.createObjectURL(b);
        ready.set(key, url);
        return url;
      }));
    }
    photos.get(key)!.then(() => live && loaded((n) => n + 1), () => photos.delete(key));
    return () => { live = false; };
  }, [key, authFetch]);

  const src = key ? ready.get(key) : undefined;
  return (
    <span className="avatar" style={{ width: size, height: size, fontSize: Math.round(size * 0.46) }} aria-hidden>
      {/* eslint-disable-next-line @next/next/no-img-element -- a blob address of a 256 px photo; nothing for next/image to optimise */}
      {src ? <img src={src} alt="" /> : name.trim().charAt(0).toLocaleUpperCase("tr-TR")}
    </span>
  );
}
