/// <reference types="vite/client" />

interface ImportMetaEnv {
	readonly PROD: boolean;
	readonly VITE_API_BASE_URL?: string;
	readonly VITE_DATA_ADAPTER?: "live" | "mock";
	readonly VITE_ENTRA_CLIENT_ID?: string;
	readonly VITE_ENTRA_AUTHORITY?: string;
	readonly VITE_API_SCOPE?: string;
}

interface ImportMeta {
	readonly env: ImportMetaEnv;
}

interface Window {
	__PPGSM_CONFIG__?: {
		entraClientId?: string;
		entraAuthority?: string;
		apiScope?: string;
		apiBaseUrl?: string;
		dataAdapter?: "live" | "mock";
	};
}