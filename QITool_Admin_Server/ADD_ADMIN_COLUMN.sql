alter table public.devices
add column if not exists is_admin boolean not null default false;

create index if not exists idx_devices_is_admin
on public.devices(is_admin);
