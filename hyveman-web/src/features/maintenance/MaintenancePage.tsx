/** Maintenance windows page (FRONTEND.md §8.6). */
import { Box } from '@mui/material';
import { PageHeader } from '@/components/PageHeader/PageHeader';
import { MaintenanceSection } from './MaintenanceSection';

export default function MaintenancePage() {
  return (
    <Box>
      <PageHeader
        title="Maintenance windows"
        subtitle="Suppress alerts for planned work, per host or fleet-wide."
      />
      <MaintenanceSection />
    </Box>
  );
}
