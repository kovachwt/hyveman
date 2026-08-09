/**
 * Event detail panel (FRONTEND.md §8.3): all indexed fields plus structured
 * and raw content, always rendered as escaped text — never HTML.
 */
import {
  Box,
  Chip,
  Divider,
  Drawer,
  IconButton,
  Stack,
  Tab,
  Tabs,
  Typography,
} from '@mui/material';
import Close from '@mui/icons-material/Close';
import { useState } from 'react';
import type { EventDto } from '@/api/generated/endpoints';
import { HealthBadge } from '@/components/HealthBadge/HealthBadge';
import { TimeDisplay } from '@/components/TimeDisplay/TimeDisplay';
import { formatCount } from '@/lib/format';

function KeyValue({ k, v }: { k: string; v: React.ReactNode }) {
  return (
    <Stack direction="row" sx={{ gap: 2, py: 0.5, alignItems: 'flex-start' }}>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 130, flexShrink: 0 }}>
        {k}
      </Typography>
      <Typography variant="body2" component="span" sx={{ wordBreak: 'break-word' }}>
        {v}
      </Typography>
    </Stack>
  );
}

function JsonBlock({ label, json, testId }: { label: string; json: string | null | undefined; testId: string }) {
  if (!json) return null;
  let pretty = json;
  try {
    pretty = JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    // Keep the raw escaped text.
  }
  return (
    <Box sx={{ mt: 1 }}>
      <Typography variant="overline" color="text.secondary">{label}</Typography>
      <Box
        component="pre"
        data-testid={testId}
        sx={{
          maxHeight: 320,
          overflow: 'auto',
          p: 1.5,
          borderRadius: 1,
          bgcolor: 'action.hover',
          fontSize: 12,
          lineHeight: 1.5,
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
          m: 0,
        }}
      >
        {pretty}
      </Box>
    </Box>
  );
}

export function EventDetailPanel({
  event,
  onClose,
}: {
  event: EventDto | null;
  onClose: () => void;
}) {
  const [tab, setTab] = useState<'fields' | 'raw'>('fields');

  if (!event) return null;

  return (
    <Drawer
      anchor="right"
      open
      onClose={onClose}
      slotProps={{ paper: { sx: { width: { xs: '100%', sm: 520 } } } }}
    >
      <Stack direction="row" sx={{ alignItems: 'center', gap: 1, px: 2, py: 1.5, borderBottom: '1px solid', borderColor: 'divider' }}>
        <Typography variant="h6" component="h2" sx={{ flexGrow: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          Event {formatCount(event.id)}
        </Typography>
        <IconButton aria-label="Close event details" onClick={onClose}>
          <Close fontSize="small" />
        </IconButton>
      </Stack>

      <Box sx={{ p: 2, overflow: 'auto', flexGrow: 1 }}>
        <Stack direction="row" spacing={1} sx={{ mb: 1.5, flexWrap: 'wrap' }}>
          <HealthBadge state={eventSeverityState(event.severity)} size="small" label={`severity ${event.severity ?? 'n/a'}`} />
          {event.channel ? <Chip label={event.channel} size="small" variant="outlined" /> : null}
          {event.eventId != null ? <Chip label={`event ${event.eventId}`} size="small" variant="outlined" /> : null}
        </Stack>

        <KeyValue k="Time" v={<TimeDisplay time={event.time} variant="full" />} />
        <KeyValue k="Source" v={event.sourceName ?? event.sourceId} />
        <KeyValue k="Host" v={event.hostName ?? event.hostId ?? '—'} />
        <KeyValue k="Dedup scope" v={event.dedupScope} />
        <KeyValue k="Record ID" v={event.recordId} />
        <KeyValue k="Facility" v={event.facility ?? '—'} />
        <KeyValue k="Task" v={event.task != null ? formatCount(event.task) : '—'} />
        <KeyValue k="Opcode" v={event.opcode != null ? formatCount(event.opcode) : '—'} />
        <KeyValue k="Keywords" v={event.keywords ?? '—'} />

        {event.message ? (
          <Box sx={{ mt: 1.5 }}>
            <Typography variant="overline" color="text.secondary">Message</Typography>
            <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
              {event.message}
            </Typography>
          </Box>
        ) : null}

        {event.fieldsJson || event.rawJson ? (
          <Box sx={{ mt: 2 }}>
            <Tabs value={tab} onChange={(_, v) => setTab(v)} aria-label="Event payload views" sx={{ minHeight: 36 }}>
              <Tab label="Structured fields" value="fields" sx={{ minHeight: 36, py: 0 }} />
              <Tab label="Raw payload" value="raw" sx={{ minHeight: 36, py: 0 }} />
            </Tabs>
            <Divider sx={{ mb: 1 }} />
            {tab === 'fields' ? <JsonBlock label="Structured fields" json={event.fieldsJson} testId="event-fields" /> : null}
            {tab === 'raw' ? (
              <Box>
                <JsonBlock label="Raw payload" json={event.rawJson} testId="event-raw" />
                <Typography variant="caption" color="text.secondary">
                  Raw content is shown as escaped text and is not trusted as HTML.
                </Typography>
              </Box>
            ) : null}
          </Box>
        ) : null}
      </Box>
    </Drawer>
  );
}

function eventSeverityState(severity: number | string | null | undefined): string {
  const n = typeof severity === 'string' ? Number(severity) : severity;
  if (n == null || Number.isNaN(n)) return 'unknown';
  if (n <= 2) return 'critical';
  if (n === 3) return 'warning';
  return 'ok';
}
