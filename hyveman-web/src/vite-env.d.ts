/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Optional public runtime override for the API origin (FRONTEND.md §6.1). */
  readonly VITE_API_BASE_URL?: string;
  /** Build identifier shown in the UI footer (FRONTEND.md §13). */
  readonly VITE_BUILD_ID?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
