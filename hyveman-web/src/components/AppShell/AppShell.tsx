/**
 * Application shell (FRONTEND.md §4/§5): responsive drawer navigation,
 * top bar with theme toggle and admin menu, connection banner, and a build
 * identifier in the drawer footer (FRONTEND.md §13).
 */
import { useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import {
  AppBar,
  Avatar,
  Badge,
  Box,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  Stack,
  Toolbar,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import Build from '@mui/icons-material/Build';
import Dashboard from '@mui/icons-material/Dashboard';
import DarkMode from '@mui/icons-material/DarkMode';
import Dns from '@mui/icons-material/Dns';
import History from '@mui/icons-material/History';
import Key from '@mui/icons-material/Key';
import LightMode from '@mui/icons-material/LightMode';
import ListAlt from '@mui/icons-material/ListAlt';
import Logout from '@mui/icons-material/Logout';
import MenuIcon from '@mui/icons-material/Menu';
import NotificationsActive from '@mui/icons-material/NotificationsActive';
import People from '@mui/icons-material/People';
import Rule from '@mui/icons-material/Rule';
import Send from '@mui/icons-material/Send';
import Storage from '@mui/icons-material/Storage';
import Hub from '@mui/icons-material/Hub';
import type { SvgIconComponent } from '@mui/icons-material';
import { useGetApiV1Overview } from '@/api';
import { numOr } from '@/api/dto';
import { useAuth } from '@/auth/AuthProvider';
import { useThemeMode } from '@/app/providers';
import { ConnectionBanner } from '@/components/ConnectionBanner/ConnectionBanner';
import { ConfirmDialog } from '@/components/ConfirmDialog/ConfirmDialog';

const BUILD_ID = import.meta.env.VITE_BUILD_ID ?? 'dev';
const APP_VERSION = '0.1.0';

interface NavItem {
  to: string;
  label: string;
  icon: SvgIconComponent;
  end?: boolean;
  badge?: number;
}

const MAIN_NAV: NavItem[] = [
  { to: '/', label: 'Overview', icon: Dashboard, end: true },
  { to: '/hosts', label: 'Hosts', icon: Dns },
  { to: '/logs', label: 'Event log', icon: ListAlt },
  { to: '/alerts', label: 'Alerts', icon: NotificationsActive },
  { to: '/rules', label: 'Alert rules', icon: Rule },
  { to: '/notifications', label: 'Notifications', icon: Send },
  { to: '/maintenance', label: 'Maintenance', icon: Build },
];

const ADMIN_NAV: NavItem[] = [
  { to: '/admin/users', label: 'Users', icon: People },
  { to: '/admin/sources', label: 'Sources & tokens', icon: Hub },
  { to: '/admin/retention', label: 'Retention', icon: Storage },
  { to: '/admin/audit', label: 'Audit log', icon: History },
  { to: '/admin/passkeys', label: 'My passkeys', icon: Key },
];

const DRAWER_WIDTH = 248;

function NavList({ items, onNavigate }: { items: NavItem[]; onNavigate?: () => void }) {
  const location = useLocation();
  return (
    <List dense>
      {items.map((item) => {
        const active = item.end ? location.pathname === item.to : location.pathname.startsWith(item.to);
        const showBadge = typeof item.badge === 'number' && item.badge > 0;
        return (
          <ListItemButton
            key={item.to}
            component={NavLink}
            to={item.to}
            end={item.end}
            selected={active}
            onClick={onNavigate}
            sx={{ borderRadius: 1, my: 0.25 }}
          >
            <ListItemIcon sx={{ minWidth: 36 }}>
              {showBadge ? (
                <Badge
                  badgeContent={item.badge! > 99 ? '99+' : item.badge}
                  color="error"
                  overlap="rectangular"
                  sx={{ '& .MuiBadge-badge': { right: -6, top: -6 } }}
                >
                  <item.icon fontSize="small" />
                </Badge>
              ) : (
                <item.icon fontSize="small" />
              )}
            </ListItemIcon>
            <ListItemText primary={item.label} primaryTypographyProps={{ variant: 'body2', fontWeight: active ? 600 : 400 }} />
          </ListItemButton>
        );
      })}
    </List>
  );
}

function DrawerContent({ onNavigate, mainNav = MAIN_NAV }: { onNavigate?: () => void; mainNav?: NavItem[] }) {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <Toolbar>
        <Typography variant="h6" component="div" sx={{ fontWeight: 700, letterSpacing: 0.5 }}>
          Hyveman
        </Typography>
      </Toolbar>
      <Divider />
      <Box sx={{ flexGrow: 1, overflowY: 'auto', px: 1 }}>
        <Typography variant="overline" color="text.secondary" sx={{ px: 1.5, fontSize: 11 }}>
          Monitor
        </Typography>
        <NavList items={mainNav} onNavigate={onNavigate} />
        <Typography variant="overline" color="text.secondary" sx={{ px: 1.5, fontSize: 11 }}>
          Admin
        </Typography>
        <NavList items={ADMIN_NAV} onNavigate={onNavigate} />
      </Box>
      <Divider />
      <Box sx={{ px: 2, py: 1.25 }}>
        <Typography variant="caption" color="text.secondary">
          hyveman-web {APP_VERSION}
          <br />
          build {BUILD_ID}
        </Typography>
      </Box>
    </Box>
  );
}

export function AppShell() {
  const theme = useTheme();
  const isDesktop = useMediaQuery(theme.breakpoints.up('md'));
  const [mobileOpen, setMobileOpen] = useState(false);
  const [adminMenuAnchor, setAdminMenuAnchor] = useState<HTMLElement | null>(null);
  const [logoutOpen, setLogoutOpen] = useState(false);
  const [logoutBusy, setLogoutBusy] = useState(false);
  const { session, logout } = useAuth();
  const { mode, toggleMode } = useThemeMode();
  const navigate = useNavigate();

  // Poll the fleet summary for the Alerts nav badge (unacknowledged count).
  // Shares the overview query cache with the dashboard; cheap for a small fleet. */
  const overview = useGetApiV1Overview({
    query: { refetchInterval: 60_000, select: (res) => res.data },
  });
  const unacked = numOr(overview.data?.summary?.unacknowledgedAlerts, 0);
  const mainNav = unacked > 0 ? MAIN_NAV.map((i) => (i.to === '/alerts' ? { ...i, badge: unacked } : i)) : MAIN_NAV;

  const doLogout = async () => {
    setLogoutBusy(true);
    try {
      await logout();
      navigate('/login', { replace: true });
    } finally {
      setLogoutBusy(false);
      setLogoutOpen(false);
    }
  };

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <AppBar
        position="fixed"
        color="inherit"
        elevation={0}
        sx={{
          zIndex: (t) => t.zIndex.drawer + 1,
          borderBottom: '1px solid',
          borderColor: 'divider',
          bgcolor: 'background.paper',
        }}
      >
        <Toolbar variant="dense" sx={{ gap: 1 }}>
          {!isDesktop ? (
            <IconButton
              aria-label="Open navigation"
              edge="start"
              onClick={() => setMobileOpen(true)}
            >
              <MenuIcon />
            </IconButton>
          ) : null}
          <Typography variant="subtitle1" sx={{ fontWeight: 600, flexGrow: 1 }} noWrap>
            Hyveman operations console
          </Typography>
          <Tooltip title={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}>
            <IconButton onClick={toggleMode} aria-label="Toggle color theme">
              {mode === 'dark' ? <LightMode fontSize="small" /> : <DarkMode fontSize="small" />}
            </IconButton>
          </Tooltip>
          <Tooltip title="Admin menu">
            <IconButton
              aria-label="Admin menu"
              onClick={(e) => setAdminMenuAnchor(e.currentTarget)}
              size="small"
            >
              <Avatar sx={{ width: 28, height: 28, bgcolor: 'primary.main', fontSize: 14 }}>
                {session?.user?.name?.[0]?.toUpperCase() ?? 'A'}
              </Avatar>
            </IconButton>
          </Tooltip>
          <Menu
            anchorEl={adminMenuAnchor}
            open={Boolean(adminMenuAnchor)}
            onClose={() => setAdminMenuAnchor(null)}
          >
            <MenuItem disabled>Signed in as {session?.user?.displayName ?? session?.user?.name ?? 'admin'}</MenuItem>
            <MenuItem onClick={() => { setAdminMenuAnchor(null); setLogoutOpen(true); }}>
              <ListItemIcon><Logout fontSize="small" /></ListItemIcon>
              Sign out
            </MenuItem>
          </Menu>
        </Toolbar>
      </AppBar>

      <Box component="nav" aria-label="Primary" sx={{ width: { md: DRAWER_WIDTH }, flexShrink: { md: 0 } }}>
        {isDesktop ? (
          <Drawer variant="permanent" open sx={{ width: DRAWER_WIDTH, '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box' } }}>
            <DrawerContent mainNav={mainNav} />
          </Drawer>
        ) : (
          <Drawer
            variant="temporary"
            open={mobileOpen}
            onClose={() => setMobileOpen(false)}
            ModalProps={{ keepMounted: true }}
            sx={{ '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box' } }}
          >
            <DrawerContent onNavigate={() => setMobileOpen(false)} mainNav={mainNav} />
          </Drawer>
        )}
      </Box>

      <Box component="main" sx={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        <ConnectionBanner />
        <Toolbar variant="dense" />
        <Box sx={{ p: { xs: 2, md: 3 }, flexGrow: 1 }}>
          <Outlet />
        </Box>
        <Stack direction="row" sx={{ px: 3, py: 1.5, borderTop: '1px solid', borderColor: 'divider', color: 'text.disabled' }} justifyContent="space-between">
          <Typography variant="caption">Hyveman operations console</Typography>
          <Typography variant="caption">hyveman-web {APP_VERSION} · build {BUILD_ID}</Typography>
        </Stack>
      </Box>

      <ConfirmDialog
        open={logoutOpen}
        title="Sign out?"
        body="You will need a passkey to sign in again."
        confirmLabel="Sign out"
        danger
        busy={logoutBusy}
        onConfirm={() => void doLogout()}
        onCancel={() => setLogoutOpen(false)}
      />
    </Box>
  );
}
