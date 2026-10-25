-- Aim Trainer leaderboard database.
-- Run this once in Supabase: SQL Editor > New query > paste everything > Run.
-- Every write goes through the functions at the bottom, so the website can't be used to cheat
-- around the rules (fake owner, editing other people's scores, skipping the score-code check).

create extension if not exists pgcrypto;

-- ---------- tables ----------
create table if not exists public.profiles (
  id uuid primary key references auth.users on delete cascade,
  username text not null default 'Player',
  avatar_url text,
  role text not null default 'player' check (role in ('owner', 'mod', 'player')),
  banned boolean not null default false,
  created_at timestamptz not null default now()
);
-- There can only ever be one owner.
create unique index if not exists profiles_one_owner on public.profiles ((role)) where role = 'owner';

create table if not exists public.scores (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.profiles on delete cascade,
  code text not null unique,
  ign text not null,
  game text not null,
  mode text not null check (mode in ('T', 'F', 'B', 'R')),
  diff int not null,
  score numeric not null,
  sens numeric not null,
  dpi int not null,
  played_at timestamptz not null,
  clip_url text,
  clip_path text,
  status text not null default 'pending' check (status in ('pending', 'verified', 'rejected')),
  reviewed_by uuid references public.profiles,
  reviewed_at timestamptz,
  created_at timestamptz not null default now(),
  check (clip_url is not null or clip_path is not null)
);
create index if not exists scores_board on public.scores (game, mode, diff);
create index if not exists scores_user on public.scores (user_id);

-- ---------- reading: everyone can see the leaderboard ----------
alter table public.profiles enable row level security;
alter table public.scores enable row level security;
drop policy if exists "anyone reads profiles" on public.profiles;
create policy "anyone reads profiles" on public.profiles for select using (true);
drop policy if exists "anyone reads scores" on public.scores;
create policy "anyone reads scores" on public.scores for select using (true);
-- No insert/update/delete policies on purpose: changes only happen through the functions below.

-- ---------- accounts: the very first account to sign up becomes the owner ----------
create or replace function public.handle_new_user() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  lock table public.profiles in share row exclusive mode;   -- two sign-ups at once can't both become owner
  insert into public.profiles (id, username, avatar_url, role)
  values (
    new.id,
    left(coalesce(new.raw_user_meta_data -> 'custom_claims' ->> 'global_name',
                  new.raw_user_meta_data ->> 'full_name',
                  new.raw_user_meta_data ->> 'name',
                  split_part(new.email, '@', 1),
                  'Player'), 32),
    new.raw_user_meta_data ->> 'avatar_url',
    case when exists (select 1 from public.profiles where role = 'owner') then 'player' else 'owner' end
  );
  return new;
end $$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created after insert on auth.users
  for each row execute function public.handle_new_user();

-- Keep names and avatars fresh when people change them on Discord.
create or replace function public.handle_user_update() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  update public.profiles set
    username = left(coalesce(new.raw_user_meta_data -> 'custom_claims' ->> 'global_name',
                             new.raw_user_meta_data ->> 'full_name',
                             new.raw_user_meta_data ->> 'name', username), 32),
    avatar_url = coalesce(new.raw_user_meta_data ->> 'avatar_url', avatar_url)
  where id = new.id;
  return new;
end $$;

drop trigger if exists on_auth_user_updated on auth.users;
create trigger on_auth_user_updated after update of raw_user_meta_data on auth.users
  for each row execute function public.handle_user_update();

-- ---------- helpers ----------
create or replace function public.my_role() returns text
language sql stable security definer set search_path = public as $$
  select role from public.profiles where id = auth.uid() and not banned
$$;

create or replace function public.is_staff() returns boolean
language sql stable security definer set search_path = public as $$
  select coalesce((select role in ('owner', 'mod') from public.profiles where id = auth.uid() and not banned), false)
$$;

-- Same checksum the app uses for score codes.
create or replace function public.fnv32(t text) returns text
language plpgsql immutable as $$
declare
  h bigint := 2166136261;
  b bytea := convert_to(t, 'UTF8');
  i int;
begin
  for i in 0 .. length(b) - 1 loop
    h := ((h # get_byte(b, i)) * 16777619) % 4294967296;
  end loop;
  return lpad(to_hex(h), 8, '0');
end $$;

-- ---------- actions ----------
create or replace function public.submit_score(p_code text, p_clip_url text default null, p_clip_path text default null)
returns uuid language plpgsql security definer set search_path = public as $$
declare
  me public.profiles;
  m text[];
  b64 text;
  payload text;
  f text[];
  new_id uuid;
begin
  select * into me from public.profiles where id = auth.uid();
  if me.id is null then raise exception 'Log in first.'; end if;
  if me.banned then raise exception 'This account is banned from submitting scores.'; end if;
  p_clip_url := nullif(trim(coalesce(p_clip_url, '')), '');
  p_clip_path := nullif(trim(coalesce(p_clip_path, '')), '');
  if p_clip_url is null and p_clip_path is null then raise exception 'Add a clip of the run.'; end if;
  if p_clip_url is not null and p_clip_url !~* '^https://' then raise exception 'Clip links must start with https://'; end if;
  if p_clip_path is not null and split_part(p_clip_path, '/', 1) <> auth.uid()::text then raise exception 'That clip upload is not yours.'; end if;

  m := regexp_match(trim(p_code), '^AIM1\.([A-Za-z0-9_-]+)\.([0-9a-f]{8})$');
  if m is null then raise exception 'That is not a score code. It should start with AIM1.'; end if;
  b64 := translate(m[1], '-_', '+/');
  b64 := b64 || repeat('=', (4 - length(b64) % 4) % 4);
  begin
    payload := convert_from(decode(b64, 'base64'), 'UTF8');
  exception when others then
    raise exception 'That code is damaged. Copy it again from the app.';
  end;
  if public.fnv32('aimtrainer-board-v1-7Qp2xK' || payload) <> m[2] then
    raise exception 'This code does not check out. It was changed or copied wrong.';
  end if;
  f := string_to_array(payload, '|');
  if coalesce(array_length(f, 1), 0) <> 9 or f[1] <> 'v1' or f[4] not in ('T', 'F', 'B', 'R')
     or f[3] not in ('minecraft', 'roblox', 'cs2', 'valorant', 'fortnite', 'overwatch', 'apex', 'custom') then
    raise exception 'This code is from a different version of the app.';
  end if;

  insert into public.scores (user_id, code, ign, game, mode, diff, score, sens, dpi, played_at, clip_url, clip_path)
  values (me.id, trim(p_code), left(f[2], 16), f[3], f[4], f[5]::int, f[6]::numeric, f[7]::numeric, f[8]::int,
          to_timestamp(f[9]::bigint), p_clip_url, p_clip_path)
  returning id into new_id;
  return new_id;
exception when unique_violation then
  raise exception 'This code was already submitted.';
end $$;

create or replace function public.review_score(p_id uuid, p_status text)
returns void language plpgsql security definer set search_path = public as $$
begin
  if not public.is_staff() then raise exception 'Only mods and the owner can review scores.'; end if;
  if p_status not in ('pending', 'verified', 'rejected') then raise exception 'Unknown status.'; end if;
  update public.scores set status = p_status, reviewed_by = auth.uid(), reviewed_at = now() where id = p_id;
end $$;

-- Returns the clip's storage path so the page can remove the file too.
create or replace function public.delete_score(p_id uuid)
returns text language plpgsql security definer set search_path = public as $$
declare s public.scores;
begin
  select * into s from public.scores where id = p_id;
  if s.id is null then return null; end if;
  if s.user_id <> auth.uid() and coalesce(public.my_role(), '') <> 'owner' then
    raise exception 'You can only delete your own scores.';
  end if;
  delete from public.scores where id = p_id;
  return s.clip_path;
end $$;

create or replace function public.set_role(p_user uuid, p_role text)
returns void language plpgsql security definer set search_path = public as $$
begin
  if coalesce(public.my_role(), '') <> 'owner' then raise exception 'Only the owner can change roles.'; end if;
  if p_role not in ('mod', 'player') then raise exception 'Roles can only be mod or player.'; end if;
  if p_user = auth.uid() then raise exception 'You can''t change your own role.'; end if;
  update public.profiles set role = p_role where id = p_user;
end $$;

create or replace function public.set_banned(p_user uuid, p_banned boolean)
returns void language plpgsql security definer set search_path = public as $$
begin
  if coalesce(public.my_role(), '') <> 'owner' then raise exception 'Only the owner can ban players.'; end if;
  if p_user = auth.uid() then raise exception 'You can''t ban yourself.'; end if;
  update public.profiles set banned = p_banned, role = case when p_banned then 'player' else role end where id = p_user;
end $$;

revoke execute on function public.fnv32(text) from anon;
grant execute on function public.submit_score(text, text, text), public.review_score(uuid, text), public.delete_score(uuid),
  public.set_role(uuid, text), public.set_banned(uuid, boolean), public.my_role(), public.is_staff() to authenticated;

-- ---------- clip uploads (public to watch, 20 MB max, MP4/WebM) ----------
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('clips', 'clips', true, 20971520, array['video/mp4', 'video/webm'])
on conflict (id) do update set public = true, file_size_limit = 20971520, allowed_mime_types = array['video/mp4', 'video/webm'];

drop policy if exists "players upload their own clips" on storage.objects;
create policy "players upload their own clips" on storage.objects for insert to authenticated
  with check (bucket_id = 'clips'
              and (storage.foldername(name))[1] = auth.uid()::text
              and not exists (select 1 from public.profiles where id = auth.uid() and banned));

drop policy if exists "players or staff delete clips" on storage.objects;
create policy "players or staff delete clips" on storage.objects for delete to authenticated
  using (bucket_id = 'clips' and ((storage.foldername(name))[1] = auth.uid()::text or public.is_staff()));
