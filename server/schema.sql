-- Run once in the Supabase SQL editor. Tables hold metadata only; conversation packages live in R2.
create extension if not exists pgcrypto;

create table if not exists public.projects (
  id uuid primary key default gen_random_uuid(),
  owner_id uuid not null references auth.users(id) on delete cascade,
  name text not null check (length(name) between 1 and 120),
  created_at timestamptz not null default now(),
  unique (owner_id, name)
);

create table if not exists public.devices (
  id uuid primary key default gen_random_uuid(),
  owner_id uuid not null references auth.users(id) on delete cascade,
  name text not null check (length(name) between 1 and 120),
  created_at timestamptz not null default now()
);

create table if not exists public.sessions (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects(id) on delete cascade,
  cli_session_id uuid not null,
  desktop_session_id text not null,
  title text not null default '',
  head_version_id uuid,
  created_at timestamptz not null default now(),
  unique (project_id, cli_session_id)
);

create table if not exists public.session_versions (
  id uuid primary key default gen_random_uuid(),
  session_id uuid not null references public.sessions(id) on delete cascade,
  parent_version_id uuid references public.session_versions(id),
  transcript_hash text not null check (transcript_hash ~ '^[0-9a-f]{64}$'),
  package_hash text not null check (package_hash ~ '^[0-9a-f]{64}$'),
  blob_key text not null unique,
  author_device_id uuid not null references public.devices(id),
  created_at timestamptz not null default now()
);

do $$ begin
  if not exists (select 1 from pg_constraint where conname = 'sessions_head_version_fk') then
    alter table public.sessions add constraint sessions_head_version_fk
      foreign key (head_version_id) references public.session_versions(id);
  end if;
end $$;

create table if not exists public.device_session_links (
  device_id uuid not null references public.devices(id) on delete cascade,
  session_id uuid not null references public.sessions(id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (device_id, session_id)
);

create index if not exists sessions_project_idx on public.sessions(project_id);
create index if not exists versions_session_idx on public.session_versions(session_id, created_at desc);
create index if not exists links_session_idx on public.device_session_links(session_id);

alter table public.projects enable row level security;
alter table public.devices enable row level security;
alter table public.sessions enable row level security;
alter table public.session_versions enable row level security;
alter table public.device_session_links enable row level security;

drop policy if exists projects_owner on public.projects;
drop policy if exists devices_owner on public.devices;
drop policy if exists sessions_owner on public.sessions;
drop policy if exists versions_owner on public.session_versions;
drop policy if exists links_owner on public.device_session_links;

create policy projects_owner on public.projects for all to authenticated
  using (owner_id = auth.uid()) with check (owner_id = auth.uid());
create policy devices_owner on public.devices for all to authenticated
  using (owner_id = auth.uid()) with check (owner_id = auth.uid());
create policy sessions_owner on public.sessions for all to authenticated
  using (exists (select 1 from public.projects p where p.id = project_id and p.owner_id = auth.uid()))
  with check (exists (select 1 from public.projects p where p.id = project_id and p.owner_id = auth.uid()));
create policy versions_owner on public.session_versions for select to authenticated
  using (exists (
    select 1 from public.sessions s join public.projects p on p.id = s.project_id
    where s.id = session_id and p.owner_id = auth.uid()
  ));
create policy links_owner on public.device_session_links for all to authenticated
  using (exists (
    select 1 from public.devices d join public.sessions s on s.id = session_id
    join public.projects p on p.id = s.project_id
    where d.id = device_id and d.owner_id = auth.uid() and p.owner_id = auth.uid()
  ))
  with check (exists (
    select 1 from public.devices d join public.sessions s on s.id = session_id
    join public.projects p on p.id = s.project_id
    where d.id = device_id and d.owner_id = auth.uid() and p.owner_id = auth.uid()
  ));

revoke insert, update, delete on public.session_versions from anon, authenticated;
revoke insert, update, delete on public.sessions from anon, authenticated;
grant select on public.projects, public.devices, public.sessions,
  public.session_versions, public.device_session_links to authenticated;
grant insert, update, delete on public.projects, public.devices,
  public.device_session_links to authenticated;
grant insert(project_id, cli_session_id, desktop_session_id, title) on public.sessions to authenticated;
grant update(title) on public.sessions to authenticated;

-- Compare-and-swap: two devices cannot silently replace each other's head.
create or replace function public.publish_version(
  p_owner_id uuid,
  p_session_id uuid,
  p_expected_head uuid,
  p_transcript_hash text,
  p_package_hash text,
  p_blob_key text,
  p_device_id uuid
) returns public.session_versions
language plpgsql security definer set search_path = public
as $$
declare
  v_session public.sessions%rowtype;
  v_version public.session_versions%rowtype;
begin
  select s.* into v_session
    from public.sessions s join public.projects p on p.id = s.project_id
    where s.id = p_session_id and p.owner_id = p_owner_id
    for update of s;
  if not found then raise exception 'session not found' using errcode = 'P0002'; end if;
  if v_session.head_version_id is distinct from p_expected_head then
    raise exception 'version conflict' using errcode = '40001';
  end if;
  if not exists (select 1 from public.devices d where d.id = p_device_id and d.owner_id = p_owner_id) then
    raise exception 'device not found' using errcode = 'P0002';
  end if;
  insert into public.session_versions
    (session_id, parent_version_id, transcript_hash, package_hash, blob_key, author_device_id)
  values (p_session_id, p_expected_head, p_transcript_hash, p_package_hash, p_blob_key, p_device_id)
  returning * into v_version;
  update public.sessions set head_version_id = v_version.id where id = p_session_id;
  return v_version;
end;
$$;
revoke all on function public.publish_version(uuid,uuid,uuid,text,text,text,uuid) from public, anon, authenticated;
grant execute on function public.publish_version(uuid,uuid,uuid,text,text,text,uuid) to service_role;
