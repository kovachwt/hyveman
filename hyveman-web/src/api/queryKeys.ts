/**
 * Query keys (FRONTEND.md §6.3): every server-side filter that changes a
 * result is part of the key. These delegates to the Orval-generated key
 * builders so invalidation always matches the keys the generated hooks use.
 */
import {
  getGetApiV1AlertsIdQueryKey,
  getGetApiV1AlertsQueryKey,
  getGetApiV1AuditLogQueryKey,
  getGetApiV1AuthPasskeysQueryKey,
  getGetApiV1AuthSessionQueryKey,
  getGetApiV1EventsIdQueryKey,
  getGetApiV1EventsQueryKey,
  getGetApiV1HostsIdHealthHistoryQueryKey,
  getGetApiV1HostsIdHealthQueryKey,
  getGetApiV1HostsIdQueryKey,
  getGetApiV1HostsIdVmsQueryKey,
  getGetApiV1HostsQueryKey,
  getGetApiV1LogonStatsQueryKey,
  getGetApiV1MaintenanceWindowsQueryKey,
  getGetApiV1NotificationChannelsQueryKey,
  getGetApiV1OverviewQueryKey,
  getGetApiV1RegistrationTokensQueryKey,
  getGetApiV1RulesQueryKey,
  getGetApiV1SavedSearchesQueryKey,
  getGetApiV1SettingsRetentionQueryKey,
  getGetApiV1SourcesQueryKey,
} from './generated/endpoints';

export const queryKeys = {
  session: getGetApiV1AuthSessionQueryKey,
  passkeys: getGetApiV1AuthPasskeysQueryKey,
  overview: getGetApiV1OverviewQueryKey,
  hosts: getGetApiV1HostsQueryKey,
  host: getGetApiV1HostsIdQueryKey,
  hostVms: getGetApiV1HostsIdVmsQueryKey,
  hostHealth: getGetApiV1HostsIdHealthQueryKey,
  healthHistory: getGetApiV1HostsIdHealthHistoryQueryKey,
  events: getGetApiV1EventsQueryKey,
  event: getGetApiV1EventsIdQueryKey,
  savedSearches: getGetApiV1SavedSearchesQueryKey,
  sources: getGetApiV1SourcesQueryKey,
  registrationTokens: getGetApiV1RegistrationTokensQueryKey,
  alerts: getGetApiV1AlertsQueryKey,
  alert: getGetApiV1AlertsIdQueryKey,
  rules: getGetApiV1RulesQueryKey,
  channels: getGetApiV1NotificationChannelsQueryKey,
  maintenanceWindows: getGetApiV1MaintenanceWindowsQueryKey,
  retention: getGetApiV1SettingsRetentionQueryKey,
  audit: getGetApiV1AuditLogQueryKey,
  logonStats: getGetApiV1LogonStatsQueryKey,
} as const;

/** Prefixes for invalidating whole resource families after mutations. */
export const resourcePrefixes = {
  hosts: ['/api/v1/hosts'] as const,
  events: ['/api/v1/events'] as const,
  savedSearches: ['/api/v1/saved-searches'] as const,
  alerts: ['/api/v1/alerts'] as const,
  rules: ['/api/v1/rules'] as const,
  channels: ['/api/v1/notification-channels'] as const,
  maintenanceWindows: ['/api/v1/maintenance-windows'] as const,
  sources: ['/api/v1/sources'] as const,
  registrationTokens: ['/api/v1/registration-tokens'] as const,
  retention: ['/api/v1/settings/retention'] as const,
  audit: ['/api/v1/audit-log'] as const,
  passkeys: ['/api/v1/auth/passkeys'] as const,
};
