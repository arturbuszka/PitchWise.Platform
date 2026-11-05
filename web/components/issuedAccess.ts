import { InputAccess } from "@/lib/api";

// Carries a just-issued stream key from the page that created it to the match page. It is held in
// memory only, so like the API it is shown once and is gone after a reload.
const issued = new Map<string, InputAccess>();

export function rememberIssuedAccess(access: InputAccess) {
  issued.set(access.input_id, access);
}

export function takeIssuedAccess(inputIds: string[]): Record<string, InputAccess> {
  const taken: Record<string, InputAccess> = {};
  for (const id of inputIds) {
    const access = issued.get(id);
    if (access) {
      taken[id] = access;
      issued.delete(id);
    }
  }
  return taken;
}
